using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

public static class BusinessReportEndpoints
{
    public static IEndpointRouteBuilder MapBusinessReports(this IEndpointRouteBuilder endpoints)
    {
        var reports = endpoints.MapGroup("/api/reports").WithTags("Business reports");

        reports.MapGet("/schema", async Task<Ok<BusinessSchemaResponse>> (
            IBusinessReportService service,
            CancellationToken cancellationToken) =>
        {
            var schema = await service.GetSchemaAsync(cancellationToken);
            return TypedResults.Ok(schema);
        })
        .WithName("GetBusinessReportSchema")
        .WithSummary("Get the business database schema")
        .WithDescription("Returns the table and column names available to local read-only report queries.")
        .Produces<BusinessSchemaResponse>();

        reports.MapPost("/query", async Task<Results<
            Ok<BusinessReportResponse>,
            BadRequest<ProblemDetails>,
            ProblemHttpResult,
            UnprocessableEntity<ProblemDetails>>> (
            BusinessReportRequest request,
            IBusinessReportService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return TypedResults.Ok(await service.AskAsync(request, cancellationToken));
            }
            catch (ReportQueryException exception)
            {
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "The generated report query was rejected",
                    Detail = exception.Message
                });
            }
            catch (HttpRequestException exception)
            {
                return TypedResults.Problem(
                    title: "Ollama could not generate the report",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (InvalidDataException exception)
            {
                return TypedResults.Problem(
                    title: "Ollama returned an invalid report",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (SqliteException exception)
            {
                return TypedResults.UnprocessableEntity(new ProblemDetails
                {
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Title = "The report query could not run",
                    Detail = exception.Message
                });
            }
        })
        .WithName("RunBusinessReport")
        .WithSummary("Ask a business question using local SQLite data")
        .WithDescription("Uses a local Ollama model to create a SELECT-only report query and returns up to 100 rows.")
        .Produces<BusinessReportResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status502BadGateway);

        return endpoints;
    }
}
