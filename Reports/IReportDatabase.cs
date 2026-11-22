using System.Data.Common;

public enum ReportDatabaseProvider
{
    Sqlite,
    SqlServer
}

public interface IReportDatabase
{
    string Id { get; }
    string DisplayName { get; }
    string ProviderName { get; }
    ReportDatabaseProvider Provider { get; }
    bool IsDemoData { get; }
    bool CanRemove { get; }
    bool InitializeOnStart { get; }
    string BusinessRules { get; }
    IReadOnlyList<string> TestPrompts { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<DbConnection> OpenReadOnlyConnectionAsync(CancellationToken cancellationToken);
    bool IsRepairableQueryError(DbException exception);
}
