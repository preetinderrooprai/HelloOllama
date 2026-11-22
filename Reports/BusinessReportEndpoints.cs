using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

public static class BusinessReportEndpoints
{
    public static IEndpointRouteBuilder MapBusinessReports(this IEndpointRouteBuilder endpoints)
    {
        var reports = endpoints.MapGroup("/api/reports").WithTags("Business reports");

        reports.MapGet("/databases", (BusinessDatabaseCatalog catalog) =>
            TypedResults.Ok(catalog.GetOptions()))
        .WithName("GetBusinessReportDatabases")
        .WithSummary("List databases available for reports")
        .Produces<IReadOnlyList<BusinessDatabaseOption>>();

        reports.MapGet("/schema", async Task<Results<
            Ok<BusinessSchemaResponse>,
            BadRequest<ProblemDetails>>> (
            string? databaseId,
            IBusinessReportService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return TypedResults.Ok(await service.GetSchemaAsync(databaseId, cancellationToken));
            }
            catch (ReportDatabaseSelectionException exception)
            {
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "The selected report database is unavailable",
                    Detail = exception.Message
                });
            }
        })
        .WithName("GetBusinessReportSchema")
        .WithSummary("Get the business database schema")
        .WithDescription("Returns the selected database's tables, columns, and foreign-key relationships.")
        .Produces<BusinessSchemaResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest);

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
            catch (ReportDatabaseSelectionException exception)
            {
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "The selected report database is unavailable",
                    Detail = exception.Message
                });
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
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return TypedResults.Problem(
                    title: "Ollama took too long to prepare the report",
                    detail: "Try a smaller model or submit the report again.",
                    statusCode: StatusCodes.Status504GatewayTimeout);
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
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        return endpoints;
    }
}
