using System.ComponentModel.DataAnnotations;

public sealed record CreateDatabaseProfileRequest
{
    [Required, MaxLength(100)]
    public required string Name { get; init; }

    [Required, MaxLength(20)]
    public required string Provider { get; init; }

    [Required, MaxLength(4000)]
    public required string ConnectionString { get; init; }
}

public sealed record UpdateDatabaseProfileGuidanceRequest
{
    [MaxLength(2000)]
    public string Context { get; init; } = "";

    [MaxLength(2000)]
    public string Hints { get; init; } = "";

    [MaxLength(2000)]
    public string Validations { get; init; } = "";

    [MaxLength(25)]
    public List<DatabaseTodoItem>? TodoItems { get; init; } = [];

    [MaxLength(3)]
    public List<string>? TestPrompts { get; init; } = [];
}

public sealed record DatabaseTodoItem
{
    [Required, MaxLength(200)]
    public required string Text { get; init; }

    public bool IsComplete { get; init; }
}

public sealed record DatabaseProfileGuidance(
    string DatabaseId,
    string DatabaseName,
    string Context,
    string Hints,
    string Validations,
    IReadOnlyList<DatabaseTodoItem> TodoItems,
    IReadOnlyList<string> TestPrompts);

public sealed record GenerateDatabaseTestPromptsRequest
{
    [Required, MaxLength(200)]
    public required string Model { get; init; }
}

public sealed record GenerateDatabaseSafetySettingsRequest
{
    [Required, MaxLength(200)]
    public required string Model { get; init; }
}

public sealed record DatabaseProfileGuidanceDraft(
    string Context,
    string Hints,
    string Validations,
    IReadOnlyList<DatabaseTodoItem> TodoItems,
    IReadOnlyList<string> TestPrompts);

public sealed record StoredDatabaseProfile(
    string Id,
    string Name,
    string Provider,
    string ConnectionString,
    string Context = "",
    string Hints = "",
    string Validations = "",
    IReadOnlyList<DatabaseTodoItem>? TodoItems = null,
    IReadOnlyList<string>? TestPrompts = null);

public sealed class DatabaseProfileException(string message, Exception? innerException = null)
    : Exception(message, innerException);
