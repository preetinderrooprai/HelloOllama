public interface IBusinessReportService
{
    Task<BusinessSchemaResponse> GetSchemaAsync(string? databaseId, CancellationToken cancellationToken);
    Task<BusinessReportResponse> AskAsync(BusinessReportRequest request, CancellationToken cancellationToken);
}
