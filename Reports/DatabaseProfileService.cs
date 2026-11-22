using System.Data.Common;
using Microsoft.Data.Sqlite;

public sealed class DatabaseProfileService(
    BusinessDatabaseCatalog databases,
    EncryptedDatabaseProfileStore profileStore)
{
    private const string DefaultBusinessRules =
        "No business definitions are provided. Do not assume formulas or status meanings beyond the user's question.";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var profiles = await profileStore.LoadAsync(cancellationToken);
        foreach (var profile in profiles)
        {
            databases.Register(CreateDatabase(profile));
        }
    }

    public async Task<BusinessDatabaseOption> AddAsync(
        CreateDatabaseProfileRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DatabaseProfileException("Enter a name for the database profile.");
        }

        if (databases.GetOptions().Any(option =>
                string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DatabaseProfileException("A database with that name is already available.");
        }

        var id = Guid.NewGuid().ToString("N");
        var provider = NormalizeProvider(request.Provider);
        var profile = new StoredDatabaseProfile(
            id,
            name,
            provider,
            request.ConnectionString.Trim());
        var database = CreateDatabase(profile, requireExistingSqliteFile: true);

        try
        {
            await using var connection = await database.OpenReadOnlyConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or IOException or
                                           ArgumentException or InvalidOperationException or FormatException)
        {
            throw new DatabaseProfileException(
                "Could not connect with read-only access. Verify the provider, connection string, and database permissions.",
                exception);
        }

        await profileStore.AddAsync(profile, cancellationToken);
        databases.Register(database);
        return databases.GetOptions().Single(option => option.Id == id);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        var database = databases.GetDatabase(id);
        if (!database.CanRemove)
        {
            throw new DatabaseProfileException("Built-in and environment-configured databases cannot be removed.");
        }

        await profileStore.RemoveAsync(id, cancellationToken);
        databases.Remove(id);
    }

    public async Task<DatabaseProfileGuidance> GetGuidanceAsync(
        string id,
        CancellationToken cancellationToken)
    {
        EnsureEditableProfile(id);
        var profile = await profileStore.GetAsync(id, cancellationToken);
        return ToGuidance(profile);
    }

    public async Task<DatabaseProfileGuidance> UpdateGuidanceAsync(
        string id,
        UpdateDatabaseProfileGuidanceRequest request,
        CancellationToken cancellationToken)
    {
        EnsureEditableProfile(id);
        var profile = await profileStore.GetAsync(id, cancellationToken);
        var todoItems = request.TodoItems ?? [];
        if (todoItems.Count > 25)
        {
            throw new DatabaseProfileException("A profile can have at most 25 checklist items.");
        }

        var normalizedTodoItems = new List<DatabaseTodoItem>(todoItems.Count);
        foreach (var item in todoItems)
        {
            if (item is null)
            {
                throw new DatabaseProfileException("Checklist items cannot be empty.");
            }

            var text = item.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200)
            {
                throw new DatabaseProfileException(
                    "Checklist items must contain between 1 and 200 characters.");
            }

            normalizedTodoItems.Add(item with { Text = text });
        }

        var testPrompts = request.TestPrompts ?? [];
        if (testPrompts.Count > 3)
        {
            throw new DatabaseProfileException("A profile can have at most three test prompts.");
        }

        var normalizedTestPrompts = new List<string>(testPrompts.Count);
        foreach (var prompt in testPrompts)
        {
            var text = prompt?.Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200 ||
                normalizedTestPrompts.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                throw new DatabaseProfileException(
                    "Test prompts must be unique and contain between 1 and 200 characters.");
            }

            normalizedTestPrompts.Add(text);
        }

        var updatedProfile = profile with
        {
            Context = NormalizeGuidance(request.Context),
            Hints = NormalizeGuidance(request.Hints),
            Validations = NormalizeGuidance(request.Validations),
            TodoItems = normalizedTodoItems,
            TestPrompts = normalizedTestPrompts
        };
        var updatedDatabase = CreateDatabase(updatedProfile);
        await profileStore.UpdateAsync(updatedProfile, cancellationToken);
        databases.Replace(updatedDatabase);
        return ToGuidance(updatedProfile);
    }

    private static IReportDatabase CreateDatabase(
        StoredDatabaseProfile profile,
        bool requireExistingSqliteFile = false)
    {
        try
        {
            return profile.Provider switch
            {
                "sqlite" => CreateSqliteDatabase(profile, requireExistingSqliteFile),
                "sqlserver" => new SqlServerBusinessDatabase(
                    profile.Id,
                    profile.Name,
                    profile.ConnectionString,
                    businessRules: BuildBusinessRules(profile),
                    testPrompts: profile.TestPrompts),
                _ => throw new DatabaseProfileException("The saved database provider is not supported.")
            };
        }
        catch (DatabaseProfileException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        {
            throw new DatabaseProfileException(
                "The saved database profile has an invalid connection string.",
                exception);
        }
    }

    private static BusinessDatabase CreateSqliteDatabase(
        StoredDatabaseProfile profile,
        bool requireExistingFile)
    {
        var builder = new SqliteConnectionStringBuilder(profile.ConnectionString);
        if (builder.Mode == SqliteOpenMode.Memory ||
            string.IsNullOrWhiteSpace(builder.DataSource) ||
            builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            builder.DataSource.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new DatabaseProfileException("Enter a path to an existing SQLite database file.");
        }

        var path = Path.GetFullPath(builder.DataSource);
        if (requireExistingFile && !File.Exists(path))
        {
            throw new DatabaseProfileException("The SQLite database file does not exist.");
        }

        builder.DataSource = path;
        builder.Mode = SqliteOpenMode.ReadOnly;
        builder.Pooling = false;
        return BusinessDatabase.FromConnectionString(
            profile.Id,
            profile.Name,
            builder.ToString(),
            BuildBusinessRules(profile),
            profile.TestPrompts);
    }

    private void EnsureEditableProfile(string id)
    {
        var database = databases.GetDatabase(id);
        if (!database.CanRemove)
        {
            throw new DatabaseProfileException(
                "Guidance can only be edited for saved database profiles.");
        }
    }

    private static string BuildBusinessRules(StoredDatabaseProfile profile)
    {
        var sections = new List<string> { DefaultBusinessRules };
        AddSection(sections, "Database context", profile.Context);
        AddSection(sections, "Reporting hints", profile.Hints);
        AddSection(
            sections,
            "Validation guidance (informational, not enforced by the application)",
            profile.Validations);
        return string.Join("\n\n", sections);
    }

    private static void AddSection(List<string> sections, string title, string? content)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            sections.Add($"{title}:\n{content.Trim()}");
        }
    }

    private static string NormalizeGuidance(string? value)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length > 2000)
        {
            throw new DatabaseProfileException("Guidance fields cannot exceed 2,000 characters.");
        }

        return normalized;
    }

    private static DatabaseProfileGuidance ToGuidance(StoredDatabaseProfile profile) =>
        new(
            profile.Id,
            profile.Name,
            profile.Context ?? "",
            profile.Hints ?? "",
            profile.Validations ?? "",
            profile.TodoItems ?? [],
            profile.TestPrompts ?? []);

    private static string NormalizeProvider(string provider) =>
        provider.Trim().ToLowerInvariant() switch
        {
            "sqlite" => "sqlite",
            "sqlserver" or "sql server" => "sqlserver",
            _ => throw new DatabaseProfileException("Choose SQLite or SQL Server.")
        };
}
