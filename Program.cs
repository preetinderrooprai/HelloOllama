using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.OpenApi;

var builder = WebApplication.CreateBuilder(args);
var ollamaUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
if (!Uri.TryCreate(ollamaUrl, UriKind.Absolute, out var ollamaUri) ||
    (ollamaUri.Scheme != Uri.UriSchemeHttp && ollamaUri.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("OLLAMA_BASE_URL must be an absolute HTTP or HTTPS URL.");
}

var configuredDatabasePath = Environment.GetEnvironmentVariable("BUSINESS_DB_PATH");
var databases = new List<IReportDatabase>
{
    new BusinessDatabase(
        "sales-demo",
        "Sales demo",
        Path.Combine(builder.Environment.ContentRootPath, "data", "business-demo.sqlite"),
        isDemoData: true,
        businessRules: "For this sample sales database, revenue is order_items.quantity multiplied by order_items.unit_price. Unless specified otherwise, only completed orders count as sales. Dates are stored as ISO YYYY-MM-DD text.",
        initializeOnStart: true),
    new BusinessDatabase(
        "access-control-demo",
        "Access control demo",
        Path.Combine(builder.Environment.ContentRootPath, "data", "access-control-demo.sqlite"),
        isDemoData: true,
        businessRules: "This is synthetic physical access-control data. Join swipes to sites through readers and doors using the declared foreign keys. Join swipes to cardholders through cards. Attendance is precomputed in attendance_records; timestamps ending in Z are UTC. Enum values are case-sensitive and uppercase: swipes.access_result is GRANTED or DENIED; swipes.direction is IN, OUT, or ACCESS; cardholders.status is ACTIVE or INACTIVE; cards.status is ACTIVE, EXPIRED, or DISABLED; door and reader status is ONLINE or OFFLINE; door activity severity is INFO, WARNING, or ALARM. The sample contains swipe and attendance records only from 2026-11-16 through 2026-11-18; relative recent-date queries can legitimately return no rows.",
        setupScriptPath: Path.Combine(builder.Environment.ContentRootPath, "data", "access-control-demo.sql"),
        initializeOnStart: true)
};
var defaultDatabaseId = "sales-demo";
if (configuredDatabasePath is not null)
{
    databases.Add(new BusinessDatabase(
        "configured",
        "Configured SQLite database",
        configuredDatabasePath,
        isDemoData: false,
        businessRules: "No business definitions are provided. Do not assume formulas or status meanings beyond the user's question.",
        initializeOnStart: true));
    defaultDatabaseId = "configured";
}

var databaseCatalog = new BusinessDatabaseCatalog(databases, defaultDatabaseId);
var localDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
if (string.IsNullOrWhiteSpace(localDataDirectory))
{
    localDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local",
        "share");
}

var profileDirectory = Path.Combine(localDataDirectory, "OllamaDesk");
var keyDirectory = Path.Combine(profileDirectory, "DataProtectionKeys");
EnsurePrivateDirectory(profileDirectory);
EnsurePrivateDirectory(keyDirectory);

builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddProblemDetails();
builder.Services.AddDataProtection()
    .SetApplicationName("OllamaDesk.DatabaseProfiles")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
builder.Services.AddSingleton(provider => new EncryptedDatabaseProfileStore(
    Path.Combine(profileDirectory, "database-profiles.dat"),
    provider.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddSingleton(new HttpClient
{
    BaseAddress = ollamaUri,
    Timeout = TimeSpan.FromSeconds(30)
});
builder.Services.AddSingleton<MachineMetrics>();
builder.Services.AddSingleton(databaseCatalog);
builder.Services.AddSingleton<DatabaseProfileService>();
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

var databaseProfiles = app.Services.GetRequiredService<DatabaseProfileService>();
await databaseProfiles.InitializeAsync(CancellationToken.None);
await databaseCatalog.InitializeAsync(CancellationToken.None);

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

static void EnsurePrivateDirectory(string path)
{
    Directory.CreateDirectory(path);
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

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
