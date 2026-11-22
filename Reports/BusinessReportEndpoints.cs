using System.Data.Common;
using System.Net;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

public static class BusinessReportEndpoints
{
    public static IEndpointRouteBuilder MapBusinessReports(this IEndpointRouteBuilder endpoints)
    {
        var reports = endpoints.MapGroup("/api/reports").WithTags("Database Chat");

        reports.MapGet("/databases", (BusinessDatabaseCatalog catalog) =>
            TypedResults.Ok(catalog.GetOptions()))
        .WithName("GetBusinessReportDatabases")
        .WithSummary("List databases available for reports")
        .Produces<IReadOnlyList<BusinessDatabaseOption>>();

        reports.MapPost("/databases", async (
            CreateDatabaseProfileRequest request,
            HttpContext context,
            DatabaseProfileService profiles,
            CancellationToken cancellationToken) =>
        {
            if (!IsLoopbackRequest(context))
            {
                return Results.Problem(
                    title: "Database profile management is available only from this computer",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var profile = await profiles.AddAsync(request, cancellationToken);
                return Results.Created($"/api/reports/databases/{profile.Id}", profile);
            }
            catch (DatabaseProfileException exception)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "The database profile could not be added",
                    Detail = exception.Message
                });
            }
        })
        .WithName("AddBusinessReportDatabase")
        .WithSummary("Connect and save a read-only database profile")
        .Produces<BusinessDatabaseOption>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        reports.MapGet("/databases/{databaseId}/guidance", async (
            string databaseId,
            HttpContext context,
            DatabaseProfileService profiles,
            CancellationToken cancellationToken) =>
        {
            if (!IsLoopbackRequest(context))
            {
                return Results.Problem(
                    title: "Database profile management is available only from this computer",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                return Results.Ok(await profiles.GetGuidanceAsync(databaseId, cancellationToken));
            }
            catch (ReportDatabaseSelectionException exception)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "The database profile is unavailable",
                    Detail = exception.Message
                });
            }
            catch (DatabaseProfileException exception)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Database guidance is unavailable",
                    Detail = exception.Message
                });
            }
        })
        .WithName("GetDatabaseProfileGuidance")
        .WithSummary("Get editable context, hints, validations, checklist, and test prompts")
        .Produces<DatabaseProfileGuidance>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        reports.MapPut("/databases/{databaseId}/guidance", async (
            string databaseId,
            UpdateDatabaseProfileGuidanceRequest request,
            HttpContext context,
            DatabaseProfileService profiles,
            CancellationToken cancellationToken) =>
        {
            if (!IsLoopbackRequest(context))
            {
                return Results.Problem(
                    title: "Database profile management is available only from this computer",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                return Results.Ok(await profiles.UpdateGuidanceAsync(
                    databaseId,
                    request,
                    cancellationToken));
            }
            catch (ReportDatabaseSelectionException exception)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "The database profile is unavailable",
                    Detail = exception.Message
                });
            }
            catch (DatabaseProfileException exception)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Database guidance could not be saved",
                    Detail = exception.Message
                });
            }
        })
        .WithName("UpdateDatabaseProfileGuidance")
        .WithSummary("Save editable guidance and checklist items with a database profile")
        .Produces<DatabaseProfileGuidance>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        reports.MapPost("/databases/{databaseId}/test-prompts", async (
            string databaseId,
            GenerateDatabaseTestPromptsRequest request,
            HttpContext context,
            IBusinessReportService service,
            CancellationToken cancellationToken) =>
        {
            if (!IsLoopbackRequest(context))
            {
                return Results.Problem(
                    title: "Database profile management is available only from this computer",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var prompts = await service.GenerateTestPromptsAsync(
                    databaseId,
                    request.Model,
                    cancellationToken);
                return Results.Ok(prompts);
            }
            catch (ReportDatabaseSelectionException exception)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "The database profile is unavailable",
                    Detail = exception.Message
                });
            }
            catch (DatabaseProfileException exception)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Test prompts cannot be generated for this database",
                    Detail = exception.Message
                });
            }
            catch (HttpRequestException exception)
            {
                return Results.Problem(
                    title: "Ollama could not generate test prompts",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Results.Problem(
                    title: "Ollama took too long to generate test prompts",
                    detail: "Try a smaller model or submit again.",
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (InvalidDataException exception)
            {
                return Results.Problem(
                    title: "Ollama returned invalid test prompts",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (DbException exception)
            {
                return Results.UnprocessableEntity(new ProblemDetails
                {
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Title = "The selected database schema could not be read",
                    Detail = exception.Message
                });
            }
        })
        .WithName("GenerateDatabaseTestPrompts")
        .WithSummary("Generate three report test prompts from a saved database profile")
        .Produces<IReadOnlyList<string>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        reports.MapPost("/databases/{databaseId}/safety-settings-draft", async (
            string databaseId,
            GenerateDatabaseSafetySettingsRequest request,
            HttpContext context,
            IBusinessReportService service,
            CancellationToken cancellationToken) =>
        {
            if (!IsLoopbackRequest(context))
            {
                return Results.Problem(
                    title: "Database profile management is available only from this computer",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var draft = await service.GenerateDatabaseSafetySettingsAsync(
                    databaseId,
                    request.Model,
                    cancellationToken);
                return Results.Ok(draft);
            }
            catch (ReportDatabaseSelectionException exception)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "The database profile is unavailable",
                    Detail = exception.Message
                });
            }
            catch (DatabaseProfileException exception)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Chat safety settings cannot be generated for this database",
                    Detail = exception.Message
                });
            }
            catch (HttpRequestException exception)
            {
                return Results.Problem(
                    title: "Ollama could not generate chat safety settings",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Results.Problem(
                    title: "Ollama took too long to generate chat safety settings",
                    detail: "Try a smaller model or submit again.",
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (InvalidDataException exception)
            {
                return Results.Problem(
                    title: "Ollama returned invalid chat safety settings",
                    detail: exception.Message,
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (DbException exception)
            {
                return Results.UnprocessableEntity(new ProblemDetails
                {
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Title = "The selected database schema could not be read",
                    Detail = exception.Message
                });
            }
        })
        .WithName("GenerateDatabaseSafetySettingsDraft")
        .WithSummary("Generate starter chat safety settings from a saved database schema")
        .Produces<DatabaseProfileGuidanceDraft>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        reports.MapDelete("/databases/{databaseId}", async (
            string databaseId,
            HttpContext context,
            DatabaseProfileService profiles,
            CancellationToken cancellationToken) =>
        {
            if (!IsLoopbackRequest(context))
            {
                return Results.Problem(
                    title: "Database profile management is available only from this computer",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                await profiles.RemoveAsync(databaseId, cancellationToken);
                return Results.NoContent();
            }
            catch (ReportDatabaseSelectionException exception)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "The database profile is unavailable",
                    Detail = exception.Message
                });
            }
            catch (DatabaseProfileException exception)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "The database profile could not be removed",
                    Detail = exception.Message
                });
            }
        })
        .WithName("RemoveBusinessReportDatabase")
        .WithSummary("Remove a saved profile without deleting its database")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        reports.MapGet("/schema", async Task<Results<
            Ok<BusinessSchemaResponse>,
            BadRequest<ProblemDetails>,
            UnprocessableEntity<ProblemDetails>>> (
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
            catch (DbException exception)
            {
                return TypedResults.UnprocessableEntity(new ProblemDetails
                {
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Title = "The selected database schema could not be read",
                    Detail = exception.Message
                });
            }
        })
        .WithName("GetBusinessReportSchema")
        .WithSummary("Get the business database schema")
        .WithDescription("Returns the selected database's tables, columns, and foreign-key relationships.")
        .Produces<BusinessSchemaResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

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
            catch (DbException exception)
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
        .WithSummary("Ask a business question using a read-only database")
        .WithDescription("Uses a local Ollama model to create a provider-specific SELECT-only query and returns up to 100 rows.")
        .Produces<BusinessReportResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        return endpoints;
    }

    private static bool IsLoopbackRequest(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } remoteAddress &&
        IPAddress.IsLoopback(remoteAddress);
}
