public interface IBusinessReportService
{
    Task<BusinessSchemaResponse> GetSchemaAsync(string? databaseId, CancellationToken cancellationToken);
    Task<BusinessReportResponse> AskAsync(BusinessReportRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GenerateTestPromptsAsync(
        string databaseId,
        string model,
        CancellationToken cancellationToken);
    Task<DatabaseProfileGuidanceDraft> GenerateDatabaseSafetySettingsAsync(
        string databaseId,
        string model,
        CancellationToken cancellationToken);
}
