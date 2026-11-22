using System.Net.Http.Json;
using System.Data.Common;
using System.Text.Json;

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
        "WRITEFILE", "INTO", "EXEC", "EXECUTE", "OPENROWSET", "OPENDATASOURCE", "OPENQUERY",
        "DBCC", "USE", "WAITFOR", "DECLARE", "CURSOR", "GO", "MERGE", "GRANT", "REVOKE",
        "DENY", "BACKUP", "RESTORE", "BULK", "KILL", "SHUTDOWN"
    };

    public Task<BusinessSchemaResponse> GetSchemaAsync(
        string? databaseId,
        CancellationToken cancellationToken) =>
        GetSchemaAsync(databases.GetDatabase(databaseId), cancellationToken);

    private static async Task<BusinessSchemaResponse> GetSchemaAsync(
        IReportDatabase database,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenReadOnlyConnectionAsync(cancellationToken);
        var tables = database.Provider switch
        {
            ReportDatabaseProvider.Sqlite => await GetSqliteSchemaAsync(connection, cancellationToken),
            ReportDatabaseProvider.SqlServer => await GetSqlServerSchemaAsync(connection, cancellationToken),
            _ => throw new InvalidOperationException("The selected database provider is not supported.")
        };

        return new BusinessSchemaResponse(
            database.Id,
            database.DisplayName,
            database.ProviderName,
            database.IsDemoData,
            tables,
            database.TestPrompts);
    }

    public async Task<IReadOnlyList<string>> GenerateTestPromptsAsync(
        string databaseId,
        string model,
        CancellationToken cancellationToken)
    {
        var database = databases.GetDatabase(databaseId);
        if (!database.CanRemove)
        {
            throw new DatabaseProfileException(
                "Test prompts can only be generated for saved database profiles.");
        }

        var schema = await GetSchemaAsync(database, cancellationToken);
        if (schema.Tables.Count == 0)
        {
            throw new DatabaseProfileException(
                "Test prompts cannot be generated because the database has no queryable tables.");
        }

        var schemaJson = JsonSerializer.Serialize(schema.Tables);
        var systemPrompt = """
            Create exactly three natural-language test questions for a read-only database reporting assistant.
            Use only the tables, columns, and reporting guidance provided. Questions must be answerable from that schema.
            Make the questions distinct and useful for testing different filters, grouping, or relationships.
            Do not include SQL, answers, sample values not present in the guidance, or references to previous questions.
            Return a JSON object with one property named "prompts", an array of exactly three short strings.
            """;
        var userPrompt = $"""
            Database provider: {database.ProviderName}
            Schema: {schemaJson}
            Reporting guidance: {database.BusinessRules}
            """;
        using var generated = await GenerateJsonAsync(
            model,
            systemPrompt,
            userPrompt,
            cancellationToken);
        return ReadGeneratedStrings(generated.RootElement, "prompts", 3);
    }

    public async Task<DatabaseProfileGuidanceDraft> GenerateDatabaseSafetySettingsAsync(
        string databaseId,
        string model,
        CancellationToken cancellationToken)
    {
        var database = databases.GetDatabase(databaseId);
        if (!database.CanRemove)
        {
            throw new DatabaseProfileException(
                "Chat safety settings can only be generated for saved database profiles.");
        }

        var schema = await GetSchemaAsync(database, cancellationToken);
        if (schema.Tables.Count == 0)
        {
            throw new DatabaseProfileException(
                "Chat safety settings cannot be generated because the database has no queryable tables.");
        }

        var systemPrompt = """
            Create useful starter settings for a read-only database chat assistant.
            Use only the database name, provider, tables, columns, and declared foreign keys provided.
            Do not infer the organization's industry, business definitions, enum meanings, or actual data values.
            Return one JSON object with exactly these properties:
            - "context": a concise description of what can be explored from the schema and what business meanings should be confirmed.
            - "hints": practical schema-grounded join and reporting hints; do not invent relationships or formulas.
            - "validations": user review checks for generated reports; these are advisory and are not enforced by the application.
            - "todoItems": an array of exactly three short, actionable review checklist strings.
            - "testPrompts": an array of exactly three distinct natural-language questions answerable from the schema.
            Keep each text field under 2,000 characters and each checklist/test prompt under 200 characters.
            Do not include SQL, answers, fabricated sample values, or database rows.
            """;
        var userPrompt = $"""
            Database name: {database.DisplayName}
            Database provider: {database.ProviderName}
            Schema: {JsonSerializer.Serialize(schema.Tables)}
            """;
        using var generated = await GenerateJsonAsync(
            model,
            systemPrompt,
            userPrompt,
            cancellationToken,
            maxTokens: 768);

        var context = ReadGeneratedText(generated.RootElement, "context", 2000);
        var hints = ReadGeneratedText(generated.RootElement, "hints", 2000);
        var validations = ReadGeneratedText(generated.RootElement, "validations", 2000);
        var todoItems = ReadGeneratedStrings(generated.RootElement, "todoItems", 3)
            .Select(text => new DatabaseTodoItem { Text = text, IsComplete = false })
            .ToArray();
        var testPrompts = ReadGeneratedStrings(generated.RootElement, "testPrompts", 3);
        return new DatabaseProfileGuidanceDraft(context, hints, validations, todoItems, testPrompts);
    }

    private async Task<JsonDocument> GenerateJsonAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken,
        int maxTokens = 384)
    {
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
                options = new { temperature = 0.2, num_predict = maxTokens }
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Ollama returned {(int)response.StatusCode}: {details}");
        }

        JsonDocument body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken)
                ?? throw new InvalidDataException("Ollama returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Ollama returned malformed JSON response data.", exception);
        }

        using var bodyDocument = body;
        if (bodyDocument.RootElement.ValueKind != JsonValueKind.Object ||
            !bodyDocument.RootElement.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var contentElement) ||
            contentElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Ollama returned a response without generated JSON content.");
        }

        var content = contentElement.GetString();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidDataException("Ollama returned empty generated JSON content.");
        }

        try
        {
            return JsonDocument.Parse(content);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Ollama returned malformed generated JSON content.", exception);
        }
    }

    private static string ReadGeneratedText(JsonElement root, string property, int maxLength)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Ollama did not return the required '{property}' text.");
        }

        var text = value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
        {
            throw new InvalidDataException(
                $"Ollama returned an empty or overlong '{property}' text.");
        }

        return text;
    }

    private static IReadOnlyList<string> ReadGeneratedStrings(
        JsonElement root,
        string property,
        int expectedCount)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(property, out var values) ||
            values.ValueKind != JsonValueKind.Array ||
            values.GetArrayLength() != expectedCount)
        {
            throw new InvalidDataException(
                $"Ollama must return exactly {expectedCount} entries in '{property}'.");
        }

        var result = new List<string>(expectedCount);
        foreach (var value in values.EnumerateArray())
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200 ||
                result.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Ollama returned an empty, duplicate, or overlong entry in '{property}'.");
            }

            result.Add(text);
        }

        return result;
    }

    private static async Task<IReadOnlyList<BusinessTableSchemaResponse>> GetSqliteSchemaAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
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

        return tables;
    }

    private static async Task<IReadOnlyList<BusinessTableSchemaResponse>> GetSqlServerSchemaAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new List<BusinessTableSchemaResponse>();
        var tableNames = new List<(string Schema, string Name)>();
        await using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = """
                SELECT TABLE_SCHEMA, TABLE_NAME
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW')
                ORDER BY TABLE_SCHEMA, TABLE_NAME;
                """;
            await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tableNames.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (schema, table) in tableNames)
        {
            var columns = new List<string>();
            await using (var columnCommand = connection.CreateCommand())
            {
                columnCommand.CommandText = """
                    SELECT COLUMN_NAME
                    FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
                    ORDER BY ORDINAL_POSITION;
                    """;
                AddParameter(columnCommand, "@schema", schema);
                AddParameter(columnCommand, "@table", table);
                await using var reader = await columnCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    columns.Add(reader.GetString(0));
                }
            }

            var foreignKeys = new List<BusinessForeignKeySchemaResponse>();
            await using (var foreignKeyCommand = connection.CreateCommand())
            {
                foreignKeyCommand.CommandText = """
                    SELECT
                        parentColumn.name,
                        referencedSchema.name,
                        referencedTable.name,
                        referencedColumn.name
                    FROM sys.foreign_key_columns AS foreignKeyColumn
                    JOIN sys.tables AS parentTable
                        ON foreignKeyColumn.parent_object_id = parentTable.object_id
                    JOIN sys.schemas AS parentSchema
                        ON parentTable.schema_id = parentSchema.schema_id
                    JOIN sys.columns AS parentColumn
                        ON foreignKeyColumn.parent_object_id = parentColumn.object_id
                        AND foreignKeyColumn.parent_column_id = parentColumn.column_id
                    JOIN sys.tables AS referencedTable
                        ON foreignKeyColumn.referenced_object_id = referencedTable.object_id
                    JOIN sys.schemas AS referencedSchema
                        ON referencedTable.schema_id = referencedSchema.schema_id
                    JOIN sys.columns AS referencedColumn
                        ON foreignKeyColumn.referenced_object_id = referencedColumn.object_id
                        AND foreignKeyColumn.referenced_column_id = referencedColumn.column_id
                    WHERE parentSchema.name = @schema AND parentTable.name = @table
                    ORDER BY foreignKeyColumn.constraint_column_id;
                    """;
                AddParameter(foreignKeyCommand, "@schema", schema);
                AddParameter(foreignKeyCommand, "@table", table);
                await using var reader = await foreignKeyCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    foreignKeys.Add(new BusinessForeignKeySchemaResponse(
                        reader.GetString(0),
                        QuoteSqlServerIdentifier(reader.GetString(1), reader.GetString(2)),
                        reader.GetString(3)));
                }
            }

            tables.Add(new BusinessTableSchemaResponse(
                QuoteSqlServerIdentifier(schema, table),
                columns,
                foreignKeys));
        }

        return tables;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string QuoteSqlServerIdentifier(string schema, string table) =>
        $"[{schema.Replace("]", "]]")}].[{table.Replace("]", "]]")}]";

    private static string QuoteSqliteIdentifier(string table) =>
        $"\"{table.Replace("\"", "\"\"")}\"";

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
                    database.ProviderName,
                    database.IsDemoData,
                    DateTimeOffset.UtcNow,
                    NeedsClarification: true);
            }

            var sql = ValidateReadOnlySql(generated.Sql, database.Provider);
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
                    database.ProviderName,
                    database.IsDemoData,
                    DateTimeOffset.UtcNow);
            }
            catch (DbException exception) when (attempt == 0 && database.IsRepairableQueryError(exception))
            {
                var correction = $"""
                    The previous SQL failed to execute with this database error: {exception.Message}
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
        IReportDatabase database,
        string model,
        string question,
        string schema,
        CancellationToken cancellationToken,
        string? correction = null)
    {
        var dialectInstructions = database.Provider switch
        {
            ReportDatabaseProvider.Sqlite =>
                "Write SQLite syntax. Use LIMIT 100 for detail rows.",
            ReportDatabaseProvider.SqlServer =>
                "Write Microsoft SQL Server T-SQL syntax. Use TOP (100) for detail rows; never use LIMIT.",
            _ => throw new InvalidOperationException("The selected database provider is not supported.")
        };
        var systemPrompt = $"""
            You write read-only {database.ProviderName} reports.
            Return one JSON object with exactly these properties: string "summary", string "sql", and boolean "needsClarification".
            The summary is a short description of the report.
            The sql must be exactly one SELECT query (a WITH clause ending in SELECT is allowed).
            Use only the listed tables and columns. Follow declared foreign-key relationships for joins, joining each foreign-key column directly to its referenced column.
            Do not add tables that are not needed to answer the question or infer relationships that are not listed.
            Treat the request as stateless: only the current question is available, not previous questions or results. Do not guess what a pronoun such as "he", "she", or "they" refers to. If a person or other required detail is not explicitly identified, set needsClarification to true, ask for the missing detail in summary, and set sql to SELECT 1 WHERE 1 = 0. Never return unrelated rows when clarification is needed.
            For counts of related events grouped by a parent entity, join through the declared foreign keys and count distinct event IDs. When the user asks for all parent entities, use LEFT JOIN so entities with no events remain visible; do not return event-level rows unless requested.
            Use exact case-sensitive enum values from the business rules; do not translate or change their casing.
            Do not write comments or a semicolon.
            Never use PRAGMA, ATTACH, extension functions, filesystem functions, or any data-changing operation.
            {dialectInstructions}
            For aggregate reports, keep the result concise.
            If the question cannot be answered from the schema, set needsClarification to true, explain the limitation in summary, and set sql to SELECT 1 WHERE 1 = 0.
            """;
        var userPrompt = $"Available {database.ProviderName} schema:\n{schema}\n\nBusiness rules:\n{database.BusinessRules}\n\nBusiness question:\n{question}";
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
        ExecuteReportAsync(IReportDatabase database, string sql, CancellationToken cancellationToken)
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

    private static string ValidateReadOnlySql(string sql, ReportDatabaseProvider provider)
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

        if ((provider == ReportDatabaseProvider.SqlServer && tokens.Contains("LIMIT")) ||
            (provider == ReportDatabaseProvider.Sqlite && tokens.Contains("TOP")))
        {
            throw new ReportQueryException("The generated query uses row-limit syntax for the wrong database provider.");
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
