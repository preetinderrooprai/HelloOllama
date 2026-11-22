public interface IBusinessReportService
{
    Task<BusinessSchemaResponse> GetSchemaAsync(CancellationToken cancellationToken);
    Task<BusinessReportResponse> AskAsync(BusinessReportRequest request, CancellationToken cancellationToken);
}
