using Microsoft.Data.Sqlite;
using System.Data.Common;

public sealed class BusinessDatabase : IReportDatabase
{
    private const string DemoSchema = """
        CREATE TABLE IF NOT EXISTS regions (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE
        );
        CREATE TABLE IF NOT EXISTS customers (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            region_id INTEGER NOT NULL REFERENCES regions(id)
        );
        CREATE TABLE IF NOT EXISTS products (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            category TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS orders (
            id INTEGER PRIMARY KEY,
            customer_id INTEGER NOT NULL REFERENCES customers(id),
            order_date TEXT NOT NULL,
            status TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS order_items (
            id INTEGER PRIMARY KEY,
            order_id INTEGER NOT NULL REFERENCES orders(id),
            product_id INTEGER NOT NULL REFERENCES products(id),
            quantity INTEGER NOT NULL,
            unit_price REAL NOT NULL
        );
        """;

    private const string DemoData = """
        INSERT OR IGNORE INTO regions (id, name) VALUES
            (1, 'North'), (2, 'South'), (3, 'West');
        INSERT OR IGNORE INTO customers (id, name, region_id) VALUES
            (1, 'Acme Office Co', 1), (2, 'Northwind Retail', 1),
            (3, 'Sunrise Stores', 2), (4, 'Harbor Supplies', 3),
            (5, 'Pinecone Market', 2);
        INSERT OR IGNORE INTO products (id, name, category) VALUES
            (1, 'Ergonomic Chair', 'Furniture'), (2, 'Standing Desk', 'Furniture'),
            (3, 'HD Monitor', 'Electronics'), (4, 'Wireless Keyboard', 'Accessories'),
            (5, 'USB-C Dock', 'Accessories');
        INSERT OR IGNORE INTO orders (id, customer_id, order_date, status) VALUES
            (1, 1, '2026-01-15', 'completed'), (2, 2, '2026-01-28', 'completed'),
            (3, 3, '2026-02-11', 'completed'), (4, 4, '2026-03-02', 'completed'),
            (5, 1, '2026-04-21', 'completed'), (6, 5, '2026-05-06', 'completed'),
            (7, 2, '2026-06-15', 'completed'), (8, 3, '2026-07-19', 'pending'),
            (9, 4, '2026-08-09', 'completed'), (10, 5, '2026-09-13', 'completed'),
            (11, 1, '2026-10-02', 'completed'), (12, 4, '2026-10-15', 'cancelled');
        INSERT OR IGNORE INTO order_items (id, order_id, product_id, quantity, unit_price) VALUES
            (1, 1, 1, 4, 249.00), (2, 1, 4, 6, 49.00),
            (3, 2, 3, 5, 329.00), (4, 2, 5, 2, 119.00),
            (5, 3, 2, 3, 599.00), (6, 3, 4, 8, 49.00),
            (7, 4, 3, 4, 329.00), (8, 4, 5, 3, 119.00),
            (9, 5, 1, 2, 249.00), (10, 5, 2, 1, 599.00),
            (11, 6, 4, 10, 49.00), (12, 6, 5, 4, 119.00),
            (13, 7, 3, 6, 329.00), (14, 8, 1, 2, 249.00),
            (15, 9, 2, 2, 599.00), (16, 9, 5, 5, 119.00),
            (17, 10, 1, 3, 249.00), (18, 11, 3, 2, 329.00),
            (19, 11, 4, 4, 49.00), (20, 12, 2, 1, 599.00);
        """;

    private readonly string _path;
    private readonly string _readOnlyConnectionString;
    private readonly bool _isDemoData;
    private readonly bool _canRemove;
    private readonly bool _initializeOnStart;
    private readonly string? _setupScriptPath;

    public BusinessDatabase(
        string id,
        string displayName,
        string path,
        bool isDemoData,
        string businessRules,
        string? setupScriptPath = null,
        bool canRemove = false,
        bool initializeOnStart = false,
        IReadOnlyList<string>? testPrompts = null)
        : this(
            id,
            displayName,
            Path.GetFullPath(path),
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(path),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString(),
            isDemoData,
            businessRules,
            setupScriptPath,
            canRemove,
            initializeOnStart,
            testPrompts)
    {
    }

    private BusinessDatabase(
        string id,
        string displayName,
        string path,
        string readOnlyConnectionString,
        bool isDemoData,
        string businessRules,
        string? setupScriptPath,
        bool canRemove,
        bool initializeOnStart,
        IReadOnlyList<string>? testPrompts)
    {
        Id = id;
        DisplayName = displayName;
        _path = path;
        _isDemoData = isDemoData;
        _canRemove = canRemove;
        _initializeOnStart = initializeOnStart;
        BusinessRules = businessRules;
        TestPrompts = testPrompts?.ToArray() ?? [];
        _setupScriptPath = setupScriptPath is null ? null : Path.GetFullPath(setupScriptPath);
        _readOnlyConnectionString = readOnlyConnectionString;
    }

    public static BusinessDatabase FromConnectionString(
        string id,
        string displayName,
        string connectionString,
        string businessRules,
        IReadOnlyList<string>? testPrompts = null)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        var path = Path.GetFullPath(builder.DataSource);
        builder.DataSource = path;
        return new BusinessDatabase(
            id,
            displayName,
            path,
            builder.ToString(),
            isDemoData: false,
            businessRules,
            setupScriptPath: null,
            canRemove: true,
            initializeOnStart: false,
            testPrompts: testPrompts);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string ProviderName => "SQLite";
    public ReportDatabaseProvider Provider => ReportDatabaseProvider.Sqlite;
    public bool IsDemoData => _isDemoData;
    public bool CanRemove => _canRemove;
    public bool InitializeOnStart => _initializeOnStart;
    public string BusinessRules { get; }
    public IReadOnlyList<string> TestPrompts { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_initializeOnStart)
        {
            return;
        }

        if (!_isDemoData && !File.Exists(_path))
        {
            throw new FileNotFoundException(
                $"The configured business database does not exist: {_path}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (!_isDemoData)
        {
            return;
        }

        if (_setupScriptPath is not null)
        {
            var setupScript = await File.ReadAllTextAsync(_setupScriptPath, cancellationToken);
            await using var setup = connection.CreateCommand();
            setup.CommandText = setupScript;
            await setup.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = DemoSchema;
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM regions;";
        if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)) > 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        await using var seed = connection.CreateCommand();
        seed.Transaction = transaction;
        seed.CommandText = DemoData;
        await seed.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task<DbConnection> OpenReadOnlyConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_readOnlyConnectionString);
        var disposeConnection = true;
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var queryOnly = connection.CreateCommand();
            queryOnly.CommandText = "PRAGMA query_only = ON;";
            await queryOnly.ExecuteNonQueryAsync(cancellationToken);
            disposeConnection = false;
            return connection;
        }
        finally
        {
            if (disposeConnection)
            {
                await connection.DisposeAsync();
            }
        }
    }

    public bool IsRepairableQueryError(DbException exception) =>
        exception is SqliteException { SqliteErrorCode: 1 };
}
