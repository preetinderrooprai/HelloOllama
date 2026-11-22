using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public sealed class BusinessReportService(
    HttpClient ollama,
    BusinessDatabase database) : IBusinessReportService
{
    private const int MaxResultRows = 100;
    private static readonly HashSet<string> ForbiddenTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ALTER", "ANALYZE", "ATTACH", "BEGIN", "COMMIT", "CREATE", "DELETE", "DETACH",
        "DROP", "INSERT", "LOAD_EXTENSION", "PRAGMA", "READFILE", "REINDEX", "RELEASE",
        "REPLACE", "ROLLBACK", "SAVEPOINT", "TRANSACTION", "TRIGGER", "UPDATE", "VACUUM",
        "WRITEFILE"
    };

    public async Task<BusinessSchemaResponse> GetSchemaAsync(CancellationToken cancellationToken)
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
            await using var columnCommand = connection.CreateCommand();
            var escapedName = tableName.Replace("\"", "\"\"");
            columnCommand.CommandText = $"PRAGMA table_info(\"{escapedName}\");";
            await using var columnReader = await columnCommand.ExecuteReaderAsync(cancellationToken);
            var columns = new List<string>();
            while (await columnReader.ReadAsync(cancellationToken))
            {
                columns.Add(columnReader.GetString(1));
            }

            tables.Add(new BusinessTableSchemaResponse(tableName, columns));
        }

        return new BusinessSchemaResponse(database.IsDemoData, tables);
    }

    public async Task<BusinessReportResponse> AskAsync(
        BusinessReportRequest request,
        CancellationToken cancellationToken)
    {
        var schema = await GetSchemaAsync(cancellationToken);
        if (schema.Tables.Count == 0)
        {
            throw new InvalidOperationException("The business database has no queryable tables.");
        }

        var schemaDescription = string.Join(
            "\n",
            schema.Tables.Select(table => $"{table.Name}({string.Join(", ", table.Columns)})"));
        var generated = await GenerateSqlAsync(
            request.Model,
            request.Question,
            schemaDescription,
            cancellationToken);
        var sql = ValidateReadOnlySql(generated.Sql);
        var (columns, rows, truncated) = await ExecuteReportAsync(sql, cancellationToken);

        return new BusinessReportResponse(
            generated.Summary,
            sql,
            columns,
            rows,
            truncated,
            database.IsDemoData,
            DateTimeOffset.UtcNow);
    }

    private async Task<(string Summary, string Sql)> GenerateSqlAsync(
        string model,
        string question,
        string schema,
        CancellationToken cancellationToken)
    {
        var systemPrompt = """
            You write read-only SQLite business reports.
            Return one JSON object with exactly these string properties: "summary" and "sql".
            The summary is a short description of the report.
            The sql must be exactly one SELECT query (a WITH clause ending in SELECT is allowed).
            Use only the listed tables and columns. Do not write comments or a semicolon.
            Never use PRAGMA, ATTACH, extension functions, filesystem functions, or any data-changing operation.
            Include a LIMIT 100 for detail rows. For aggregate reports, keep the result concise.
            In this sample sales schema, revenue is order_items.quantity multiplied by order_items.unit_price.
            Unless the question specifies otherwise, treat completed orders as sales and exclude pending or cancelled orders.
            Dates are stored as ISO YYYY-MM-DD text.
            If the question cannot be answered from the schema, return a SELECT that returns no rows and explain the limitation in summary.
            """;
        var userPrompt = $"Available SQLite schema:\n{schema}\n\nBusiness question:\n{question}";
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
                options = new { temperature = 0.1 }
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
            if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(sql))
            {
                throw new InvalidDataException("Ollama returned an incomplete report query.");
            }

            return (summary.Trim(), sql.Trim());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                           KeyNotFoundException)
        {
            throw new InvalidDataException("Ollama returned an invalid report response.", exception);
        }
    }

    private async Task<(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated)>
        ExecuteReportAsync(string sql, CancellationToken cancellationToken)
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
