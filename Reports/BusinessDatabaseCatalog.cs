public sealed record BusinessDatabaseOption(string Id, string Name);

public sealed class ReportDatabaseSelectionException(string message) : Exception(message);

public sealed class BusinessDatabaseCatalog
{
    private readonly Dictionary<string, BusinessDatabase> _databases;
    private readonly string _defaultDatabaseId;

    public BusinessDatabaseCatalog(IEnumerable<BusinessDatabase> databases, string defaultDatabaseId)
    {
        _databases = databases.ToDictionary(database => database.Id, StringComparer.OrdinalIgnoreCase);
        if (!_databases.ContainsKey(defaultDatabaseId))
        {
            throw new ArgumentException("The default database must be registered.", nameof(defaultDatabaseId));
        }

        _defaultDatabaseId = defaultDatabaseId;
    }

    public IReadOnlyList<BusinessDatabaseOption> GetOptions() =>
        _databases.Values
            .Select(database => new BusinessDatabaseOption(database.Id, database.DisplayName))
            .ToArray();

    public BusinessDatabase GetDatabase(string? databaseId)
    {
        var selectedDatabaseId = string.IsNullOrWhiteSpace(databaseId) ? _defaultDatabaseId : databaseId;
        if (!_databases.TryGetValue(selectedDatabaseId, out var database))
        {
            throw new ReportDatabaseSelectionException(
                $"Database '{selectedDatabaseId}' is not available. Choose a database from the list.");
        }

        return database;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var database in _databases.Values)
        {
            await database.InitializeAsync(cancellationToken);
        }
    }
}
