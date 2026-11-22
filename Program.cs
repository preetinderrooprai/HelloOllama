using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;

var builder = WebApplication.CreateBuilder(args);
var ollamaUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
if (!Uri.TryCreate(ollamaUrl, UriKind.Absolute, out var ollamaUri) ||
    (ollamaUri.Scheme != Uri.UriSchemeHttp && ollamaUri.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("OLLAMA_BASE_URL must be an absolute HTTP or HTTPS URL.");
}

var configuredDatabasePath = Environment.GetEnvironmentVariable("BUSINESS_DB_PATH");
var businessDatabase = new BusinessDatabase(
    configuredDatabasePath ?? Path.Combine(builder.Environment.ContentRootPath, "data", "business-demo.sqlite"),
    isDemoData: configuredDatabasePath is null);

builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(new HttpClient
{
    BaseAddress = ollamaUri,
    Timeout = TimeSpan.FromSeconds(30)
});
builder.Services.AddSingleton<MachineMetrics>();
builder.Services.AddSingleton(businessDatabase);
builder.Services.AddHttpClient<IBusinessReportService, BusinessReportService>(client =>
{
    client.BaseAddress = ollamaUri;
    client.Timeout = TimeSpan.FromMinutes(2);
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await businessDatabase.InitializeAsync(CancellationToken.None);

app.MapGet("/api/health", async (HttpClient ollama, CancellationToken cancellationToken) =>
{
    try
    {
        using var response = await ollama.GetAsync("/api/version", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (HttpRequestException)
    {
        return Results.Json(new
        {
            connected = false,
            error = $"Could not connect to Ollama at {ollama.BaseAddress}. Start Ollama or set OLLAMA_BASE_URL."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/models", (HttpClient ollama) => ProxyGetAsync(ollama, "/api/tags"));
app.MapGet("/api/running", (HttpClient ollama) => ProxyGetAsync(ollama, "/api/ps"));
app.MapGet("/api/metrics", (MachineMetrics metrics, CancellationToken cancellationToken) =>
    metrics.ReadAsync(cancellationToken));

app.MapPost("/api/chat", async (HttpContext context, HttpClient ollama) =>
    await ProxyStreamAsync(context, ollama, "/api/chat"));
app.MapPost("/api/pull", async (HttpContext context, HttpClient ollama) =>
    await ProxyStreamAsync(context, ollama, "/api/pull"));

app.MapPost("/api/models/show", async (JsonElement body, HttpClient ollama, CancellationToken cancellationToken) =>
    await ProxyJsonAsync(ollama, "/api/show", body, cancellationToken));
app.MapPost("/api/models/delete", async (JsonElement body, HttpClient ollama, CancellationToken cancellationToken) =>
    await ProxyJsonAsync(ollama, "/api/delete", body, cancellationToken));
app.MapPost("/api/models/copy", async (JsonElement body, HttpClient ollama, CancellationToken cancellationToken) =>
    await ProxyJsonAsync(ollama, "/api/copy", body, cancellationToken));
app.MapPost("/api/models/unload", async (JsonElement body, HttpClient ollama, CancellationToken cancellationToken) =>
{
    var request = JsonNode.Parse(body.GetRawText())!.AsObject();
    request["model"] = request["name"]?.DeepClone();
    request.Remove("name");
    request["keep_alive"] = 0;
    request["prompt"] = "";
    request["stream"] = false;
    return await ProxyJsonAsync(ollama, "/api/generate", JsonSerializer.SerializeToElement(request), cancellationToken);
});
app.MapBusinessReports();

app.Run();

static async Task<IResult> ProxyGetAsync(HttpClient client, string path)
{
    try
    {
        using var response = await client.GetAsync(path);
        var content = await response.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (HttpRequestException exception)
    {
        return Results.Json(new { error = $"Ollama request failed: {exception.Message}" },
            statusCode: StatusCodes.Status502BadGateway);
    }
}

static async Task<IResult> ProxyJsonAsync(
    HttpClient client,
    string path,
    JsonElement body,
    CancellationToken cancellationToken)
{
    using var requestContent = new StringContent(body.GetRawText());
    requestContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    try
    {
        using var response = await client.PostAsync(path, requestContent, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (HttpRequestException exception)
    {
        return Results.Json(new { error = $"Ollama request failed: {exception.Message}" },
            statusCode: StatusCodes.Status502BadGateway);
    }
}

static async Task ProxyStreamAsync(HttpContext context, HttpClient client, string path)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, path)
    {
        Content = new StringContent(await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted))
    };
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

    try
    {
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/x-ndjson";
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (HttpRequestException exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = $"Ollama request failed: {exception.Message}" },
            context.RequestAborted);
    }
}
