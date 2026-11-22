public sealed record BusinessDatabaseOption(
    string Id,
    string Name,
    string Provider,
    bool IsDemoData,
    bool CanRemove);

public sealed class ReportDatabaseSelectionException(string message) : Exception(message);

public sealed class BusinessDatabaseCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IReportDatabase> _databases;
    private readonly string _defaultDatabaseId;

    public BusinessDatabaseCatalog(IEnumerable<IReportDatabase> databases, string defaultDatabaseId)
    {
        _databases = databases.ToDictionary(database => database.Id, StringComparer.OrdinalIgnoreCase);
        if (!_databases.ContainsKey(defaultDatabaseId))
        {
            throw new ArgumentException("The default database must be registered.", nameof(defaultDatabaseId));
        }

        _defaultDatabaseId = defaultDatabaseId;
    }

    public IReadOnlyList<BusinessDatabaseOption> GetOptions()
    {
        lock (_gate)
        {
            return _databases.Values
                .Select(database => new BusinessDatabaseOption(
                    database.Id,
                    database.DisplayName,
                    database.ProviderName,
                    database.IsDemoData,
                    database.CanRemove))
                .ToArray();
        }
    }

    public IReportDatabase GetDatabase(string? databaseId)
    {
        var selectedDatabaseId = string.IsNullOrWhiteSpace(databaseId) ? _defaultDatabaseId : databaseId;
        lock (_gate)
        {
            if (_databases.TryGetValue(selectedDatabaseId, out var database))
            {
                return database;
            }
        }

        throw new ReportDatabaseSelectionException(
            $"Database '{selectedDatabaseId}' is not available. Choose a database from the list.");
    }

    public void Register(IReportDatabase database)
    {
        lock (_gate)
        {
            if (!_databases.TryAdd(database.Id, database))
            {
                throw new InvalidOperationException("A database profile with this ID is already registered.");
            }
        }
    }

    public void Replace(IReportDatabase database)
    {
        lock (_gate)
        {
            if (!_databases.TryGetValue(database.Id, out var existing) ||
                !existing.CanRemove ||
                !database.CanRemove)
            {
                throw new ReportDatabaseSelectionException(
                    $"Saved database '{database.Id}' is not available for editing.");
            }

            _databases[database.Id] = database;
        }
    }

    public IReportDatabase Remove(string databaseId)
    {
        lock (_gate)
        {
            if (!_databases.TryGetValue(databaseId, out var database))
            {
                throw new ReportDatabaseSelectionException(
                    $"Database '{databaseId}' is not available. Choose a database from the list.");
            }

            if (!database.CanRemove)
            {
                throw new ReportDatabaseSelectionException(
                    "Built-in and environment-configured databases cannot be removed here.");
            }

            _databases.Remove(databaseId);
            return database;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IReportDatabase[] databases;
        lock (_gate)
        {
            databases = _databases.Values.Where(database => database.InitializeOnStart).ToArray();
        }

        foreach (var database in databases)
        {
            await database.InitializeAsync(cancellationToken);
        }
    }
}
