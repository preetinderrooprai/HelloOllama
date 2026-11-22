using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public sealed class BusinessReportService(
    HttpClient ollama,
    BusinessDatabaseCatalog databases) : IBusinessReportService
{
    private const int MaxResultRows = 100;
    private static readonly HashSet<string> ForbiddenTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALTER", "ANALYZE", "ATTACH", "BEGIN", "COMMIT", "CREATE", "DELETE", "DETACH",
        "DROP", "INSERT", "LOAD_EXTENSION", "PRAGMA", "READFILE", "REINDEX", "RELEASE",
        "REPLACE", "ROLLBACK", "SAVEPOINT", "TRANSACTION", "TRIGGER", "UPDATE", "VACUUM",
        "WRITEFILE"
    };

    public Task<BusinessSchemaResponse> GetSchemaAsync(
        string? databaseId,
        CancellationToken cancellationToken) =>
        GetSchemaAsync(databases.GetDatabase(databaseId), cancellationToken);

    private static async Task<BusinessSchemaResponse> GetSchemaAsync(
        BusinessDatabase database,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenReadOnlyConnectionAsync(cancellationToken);
        var tables = new List<BusinessTableSchemaResponse>();
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        var tableNames = new List<string>();
        await using (var tableReader = await tableCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await tableReader.ReadAsync(cancellationToken))
            {
                tableNames.Add(tableReader.GetString(0));
            }
        }

        foreach (var tableName in tableNames)
        {
            var escapedName = tableName.Replace("\"", "\"\"");
            var columns = new List<string>();
            await using (var columnCommand = connection.CreateCommand())
            {
                columnCommand.CommandText = $"PRAGMA table_info(\"{escapedName}\");";
                await using var columnReader = await columnCommand.ExecuteReaderAsync(cancellationToken);
                while (await columnReader.ReadAsync(cancellationToken))
                {
                    columns.Add(columnReader.GetString(1));
                }
            }

            var foreignKeys = new List<BusinessForeignKeySchemaResponse>();
            await using (var foreignKeyCommand = connection.CreateCommand())
            {
                foreignKeyCommand.CommandText = $"PRAGMA foreign_key_list(\"{escapedName}\");";
                await using var foreignKeyReader = await foreignKeyCommand.ExecuteReaderAsync(cancellationToken);
                while (await foreignKeyReader.ReadAsync(cancellationToken))
                {
                    foreignKeys.Add(new BusinessForeignKeySchemaResponse(
                        foreignKeyReader.GetString(3),
                        foreignKeyReader.GetString(2),
                        foreignKeyReader.IsDBNull(4) ? null : foreignKeyReader.GetString(4)));
                }
            }

            tables.Add(new BusinessTableSchemaResponse(tableName, columns, foreignKeys));
        }

        return new BusinessSchemaResponse(
            database.Id,
            database.DisplayName,
            database.IsDemoData,
            tables);
    }

    public async Task<BusinessReportResponse> AskAsync(
        BusinessReportRequest request,
        CancellationToken cancellationToken)
    {
        var database = databases.GetDatabase(request.DatabaseId);
        var schema = await GetSchemaAsync(database, cancellationToken);
        if (schema.Tables.Count == 0)
        {
            throw new InvalidOperationException("The business database has no queryable tables.");
        }

        var schemaDescription = string.Join("\n", schema.Tables.Select(DescribeTableSchema));
        var generated = await GenerateSqlAsync(
            database,
            request.Model,
            request.Question,
            schemaDescription,
            cancellationToken);

        for (var attempt = 0; ; attempt++)
        {
            if (generated.NeedsClarification)
            {
                return new BusinessReportResponse(
                    generated.Summary,
                    string.Empty,
                    [],
                    [],
                    false,
                    database.Id,
                    database.DisplayName,
                    database.IsDemoData,
                    DateTimeOffset.UtcNow,
                    NeedsClarification: true);
            }

            var sql = ValidateReadOnlySql(generated.Sql);
            try
            {
                var (columns, rows, truncated) = await ExecuteReportAsync(database, sql, cancellationToken);
                return new BusinessReportResponse(
                    generated.Summary,
                    sql,
                    columns,
                    rows,
                    truncated,
                    database.Id,
                    database.DisplayName,
                    database.IsDemoData,
                    DateTimeOffset.UtcNow);
            }
            catch (SqliteException exception) when (attempt == 0 && exception.SqliteErrorCode == 1)
            {
                var correction = $"""
                    The previous SQL failed to execute with this SQLite error: {exception.Message}
                    Previous SQL: {generated.Sql}
                    Correct the query using only the listed schema. Return a different, valid read-only SELECT query.
                    """;
                generated = await GenerateSqlAsync(
                    database,
                    request.Model,
                    request.Question,
                    schemaDescription,
                    cancellationToken,
                    correction);
            }
        }
    }

    private async Task<(string Summary, string Sql, bool NeedsClarification)> GenerateSqlAsync(
        BusinessDatabase database,
        string model,
        string question,
        string schema,
        CancellationToken cancellationToken,
        string? correction = null)
    {
        var systemPrompt = """
            You write read-only SQLite business reports.
            Return one JSON object with exactly these properties: string "summary", string "sql", and boolean "needsClarification".
            The summary is a short description of the report.
            The sql must be exactly one SELECT query (a WITH clause ending in SELECT is allowed).
            Use only the listed tables and columns. Follow declared foreign-key relationships for joins, joining each foreign-key column directly to its referenced column.
            Do not add tables that are not needed to answer the question or infer relationships that are not listed.
            Treat the request as stateless: only the current question is available, not previous questions or results. Do not guess what a pronoun such as "he", "she", or "they" refers to. If a person or other required detail is not explicitly identified, set needsClarification to true, ask for the missing detail in summary, and set sql to SELECT 1 WHERE 0. Never return unrelated rows when clarification is needed.
            For counts of related events grouped by a parent entity, join through the declared foreign keys and count distinct event IDs. When the user asks for all parent entities, use LEFT JOIN so entities with no events remain visible; do not return event-level rows unless requested.
            Use exact case-sensitive enum values from the business rules; do not translate or change their casing.
            Do not write comments or a semicolon.
            Never use PRAGMA, ATTACH, extension functions, filesystem functions, or any data-changing operation.
            Include a LIMIT 100 for detail rows. For aggregate reports, keep the result concise.
            If the question cannot be answered from the schema, set needsClarification to true, explain the limitation in summary, and set sql to SELECT 1 WHERE 0.
            """;
        var userPrompt = $"Available SQLite schema:\n{schema}\n\nBusiness rules:\n{database.BusinessRules}\n\nBusiness question:\n{question}";
        if (correction is not null)
        {
            userPrompt += $"\n\nQuery correction needed:\n{correction}";
        }

        using var response = await ollama.PostAsJsonAsync(
            "/api/chat",
            new
            {
                model,
                stream = false,
                format = "json",
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                think = false,
                options = new { temperature = 0.1, num_predict = 256 }
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Ollama returned {(int)response.StatusCode}: {details}");
        }

        try
        {
            using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken)
                ?? throw new InvalidDataException("Ollama returned an empty response.");
            var content = body.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidDataException("Ollama did not return a report query.");
            }

            using var generated = JsonDocument.Parse(content);
            var summary = generated.RootElement.GetProperty("summary").GetString();
            var sql = generated.RootElement.GetProperty("sql").GetString();
            var clarification = generated.RootElement.GetProperty("needsClarification");
            if (string.IsNullOrWhiteSpace(summary) ||
                string.IsNullOrWhiteSpace(sql) ||
                clarification.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException("Ollama returned an incomplete report query.");
            }

            return (summary.Trim(), sql.Trim(), clarification.GetBoolean());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                           KeyNotFoundException)
        {
            throw new InvalidDataException("Ollama returned an invalid report response.", exception);
        }
    }

    private async Task<(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated)>
        ExecuteReportAsync(BusinessDatabase database, string sql, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 10;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();
        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count == MaxResultRows)
            {
                truncated = true;
                break;
            }

            var row = new object?[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[index] = reader.IsDBNull(index) ? null : NormalizeSqlValue(reader.GetValue(index));
            }

            rows.Add(row);
        }

        return (columns, rows, truncated);
    }

    private static object? NormalizeSqlValue(object value) => value switch
    {
        byte[] bytes => Convert.ToBase64String(bytes),
        DBNull => null,
        _ => value
    };

    private static string DescribeTableSchema(BusinessTableSchemaResponse table)
    {
        var description = $"{table.Name}({string.Join(", ", table.Columns)})";
        if (table.ForeignKeys.Count == 0)
        {
            return description;
        }

        var relationships = table.ForeignKeys.Select(foreignKey =>
        {
            var referencedColumn = foreignKey.ReferencedColumn ?? "primary key";
            return $"{table.Name}.{foreignKey.Column} -> {foreignKey.ReferencedTable}.{referencedColumn}";
        });
        return $"{description}\n  Foreign keys: {string.Join(", ", relationships)}";
    }

    private static string ValidateReadOnlySql(string sql)
    {
        if (sql.Length > 10_000)
        {
            throw new ReportQueryException("The generated SQL query is too long.");
        }

        var tokens = new List<string>();
        for (var index = 0; index < sql.Length;)
        {
            var current = sql[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '\'' || current == '"' || current == '`' || current == '[')
            {
                index = SkipQuoted(sql, index, current);
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-' ||
                current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                throw new ReportQueryException("SQL comments are not allowed in generated report queries.");
            }

            if (current == ';')
            {
                if (!string.IsNullOrWhiteSpace(sql[(index + 1)..]))
                {
                    throw new ReportQueryException("Only a single SQL query is allowed.");
                }

                break;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
                {
                    index++;
                }

                tokens.Add(sql[start..index].ToUpperInvariant());
                continue;
            }

            index++;
        }

        if (tokens.Count == 0 || tokens[0] is not ("SELECT" or "WITH") ||
            (tokens[0] == "WITH" && !tokens.Contains("SELECT")))
        {
            throw new ReportQueryException("Only a read-only SELECT report query is allowed.");
        }

        if (tokens.Any(token => ForbiddenTokens.Contains(token) ||
                                token.StartsWith("SQLITE_", StringComparison.OrdinalIgnoreCase) ||
                                token.StartsWith("PRAGMA_", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ReportQueryException("The generated query uses an operation or table that is not allowed.");
        }

        return sql.Trim().TrimEnd(';').TrimEnd();
    }

    private static int SkipQuoted(string sql, int start, char opening)
    {
        var closing = opening == '[' ? ']' : opening;
        for (var index = start + 1; index < sql.Length; index++)
        {
            if (sql[index] != closing)
            {
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == closing)
            {
                index++;
                continue;
            }

            return index + 1;
        }

        throw new ReportQueryException("The generated query contains an unterminated quoted value.");
    }
}
