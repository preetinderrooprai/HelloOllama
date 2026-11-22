using System.ComponentModel.DataAnnotations;

/// <summary>Asks a local Ollama model to create a read-only business report.</summary>
public sealed record BusinessReportRequest
{
    /// <summary>Gets the selected registered database, or null to use the configured default.</summary>
    [MaxLength(100)]
    public string? DatabaseId { get; init; }

    /// <summary>Gets the installed Ollama model to use for report generation.</summary>
    [Required, MaxLength(200)]
    public required string Model { get; init; }

    /// <summary>Gets the natural-language business question.</summary>
    [Required, MaxLength(2000)]
    public required string Question { get; init; }
}

/// <summary>Describes a table available to the business-reporting assistant.</summary>
public sealed record BusinessTableSchemaResponse(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<BusinessForeignKeySchemaResponse> ForeignKeys);

/// <summary>Describes a foreign-key relationship between database tables.</summary>
public sealed record BusinessForeignKeySchemaResponse(
    string Column,
    string ReferencedTable,
    string? ReferencedColumn);

/// <summary>Describes a selected read-only database available to the reporting assistant.</summary>
public sealed record BusinessSchemaResponse(
    string DatabaseId,
    string DatabaseName,
    string ProviderName,
    bool IsDemoData,
    IReadOnlyList<BusinessTableSchemaResponse> Tables,
    IReadOnlyList<string> TestPrompts);

/// <summary>Contains a generated read-only report query and its results.</summary>
public sealed record BusinessReportResponse(
    string Summary,
    string Sql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    string DatabaseId,
    string DatabaseName,
    string ProviderName,
    bool IsDemoData,
    DateTimeOffset GeneratedAt,
    bool NeedsClarification = false);

/// <summary>Represents a report query rejected by the read-only SQL guard.</summary>
public sealed class ReportQueryException(string message) : Exception(message);
