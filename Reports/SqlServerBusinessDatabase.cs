using System.Data.Common;
using Microsoft.Data.SqlClient;

public sealed class SqlServerBusinessDatabase : IReportDatabase
{
    private readonly string _readOnlyConnectionString;

    public SqlServerBusinessDatabase(
        string id,
        string displayName,
        string connectionString,
        bool canRemove = true,
        string businessRules = "No business definitions are provided. Do not assume formulas or status meanings beyond the user's question.",
        IReadOnlyList<string>? testPrompts = null)
    {
        Id = id;
        DisplayName = displayName;
        CanRemove = canRemove;
        BusinessRules = businessRules;
        TestPrompts = testPrompts?.ToArray() ?? [];
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationIntent = ApplicationIntent.ReadOnly,
            Pooling = false
        };
        _readOnlyConnectionString = builder.ConnectionString;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string ProviderName => "SQL Server";
    public ReportDatabaseProvider Provider => ReportDatabaseProvider.SqlServer;
    public bool IsDemoData => false;
    public bool CanRemove { get; }
    public bool InitializeOnStart => false;
    public string BusinessRules { get; }
    public IReadOnlyList<string> TestPrompts { get; }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<DbConnection> OpenReadOnlyConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_readOnlyConnectionString);
        var opened = false;
        try
        {
            await connection.OpenAsync(cancellationToken);
            opened = true;
            return connection;
        }
        finally
        {
            if (!opened)
            {
                await connection.DisposeAsync();
            }
        }
    }

    public bool IsRepairableQueryError(DbException exception) =>
        exception is SqlException { Number: 102 or 156 or 195 or 207 or 208 or 4104 };
}
