using System.Text.Json;
using Bonsai.Chat;
using Bonsai.Chat.Chat;
using Bonsai.Chat.Inference;

const int MaxMessageBodyBytes = 16 * 1024 * 1024;
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = MaxMessageBodyBytes);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection("Chat"));
builder.Services.Configure<LlamaOptions>(builder.Configuration.GetSection("Llama"));
builder.Services.Configure<ModelCatalogOptions>(builder.Configuration.GetSection("Models"));
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddSingleton<IChatModel>(services => services.GetRequiredService<ModelCatalog>());
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<ChatCoordinator>();
builder.Services.AddHostedService(services => services.GetRequiredService<ChatCoordinator>());

var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (ChatApiException error)
    {
        context.Response.StatusCode = error.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = error.Message });
    }
    catch (BadHttpRequestException error)
    {
        context.Response.StatusCode = error.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid request or message is too large." });
    }
    catch (IOException error)
    {
        app.Logger.LogError(error, "Chat storage operation failed");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "Could not read or save chat history. Check the data directory and server logs." });
    }
});
app.UseMiddleware<LocalBrowserMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (IChatModel model, ModelCatalog catalog, ChatCoordinator chats) =>
{
    var status = model.GetStatus();
    return Results.Ok(new
    {
        model = new { name = status.ModelName, online = status.IsReady, state = status.State, message = status.Message,
            context_size = catalog.SelectedContextSize },
        active_jobs = chats.ActiveJobs(),
        selected_model_id = catalog.SelectedId
    });
});
app.MapGet("/api/models", (ModelCatalog catalog) =>
    Results.Ok(new { models = catalog.ListModels(), selected_id = catalog.SelectedId }));
app.MapPost("/api/models/select", async (HttpRequest request, ModelCatalog catalog, ChatCoordinator chats) =>
{
    using var body = await ReadObjectAsync(request);
    if (!body.RootElement.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(value.GetString()))
        throw new ChatApiException(400, "Choose a model ID.");
    await chats.SelectModelAsync(catalog, value.GetString()!, request.HttpContext.RequestAborted);
    var status = catalog.GetStatus();
    return Results.Ok(new
    {
        selected_id = catalog.SelectedId,
        model = new { name = status.ModelName, online = status.IsReady, state = status.State, message = status.Message,
            context_size = catalog.SelectedContextSize }
    });
});
app.MapGet("/api/sessions", (ChatCoordinator chats) => Results.Ok(new { sessions = chats.List() }));
app.MapPost("/api/sessions", async (HttpRequest request, ChatCoordinator chats) =>
{
    using var body = await ReadObjectAsync(request);
    var title = body.RootElement.TryGetProperty("title", out var value) ? ValidTitle(value) : null;
    return Results.Json(new { session = chats.Create(title) }, statusCode: 201);
});
app.MapGet("/api/sessions/{id}", (string id, ChatCoordinator chats) => Results.Ok(new { session = chats.Detail(id) }));
app.MapPatch("/api/sessions/{id}", async (string id, HttpRequest request, ChatCoordinator chats) =>
{
    using var body = await ReadObjectAsync(request);
    var title = ValidTitle(body.RootElement.TryGetProperty("title", out var value) ? value : default);
    return Results.Ok(new { session = chats.Rename(id, title) });
});
app.MapDelete("/api/sessions/{id}", (string id, ChatCoordinator chats) =>
{
    chats.Delete(id);
    return Results.Ok(new { ok = true });
});
app.MapPost("/api/sessions/{id}/messages", async (string id, HttpRequest request, ChatCoordinator chats) =>
{
    using var body = await ReadObjectAsync(request, MaxMessageBodyBytes);
    if (!body.RootElement.TryGetProperty("content", out var value) || value.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(value.GetString()))
        throw new ChatApiException(400, "Message must contain text.");
    return Results.Json(new { job = chats.Send(id, value.GetString()!) }, statusCode: 202);
});
app.MapGet("/api/jobs/{id}", (string id, ChatCoordinator chats) => Results.Ok(new { job = chats.GetJob(id) }));
app.MapPost("/api/jobs/{id}/cancel", async (string id, ChatCoordinator chats) =>
    Results.Ok(new { job = await chats.CancelAsync(id) }));
app.MapFallback("/api/{**path}", () => Results.NotFound(new { error = "API endpoint not found." }));

app.Run();

static string ValidTitle(JsonElement value)
{
    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
        || value.GetString()!.Trim().Length > 120)
        throw new ChatApiException(400, "Title must contain 1–120 characters.");
    return value.GetString()!.Trim();
}

static async Task<JsonDocument> ReadObjectAsync(HttpRequest request, int maxBytes = 256_000)
{
    if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
        throw new ChatApiException(415, "Send an application/json request.");
    if (request.ContentLength > maxBytes) throw new ChatApiException(413, "Message is too large.");
    using var buffer = new MemoryStream();
    var chunk = new byte[8192];
    int count;
    while ((count = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
    {
        if (buffer.Length + count > maxBytes) throw new ChatApiException(413, "Message is too large.");
        buffer.Write(chunk, 0, count);
    }
    try
    {
        var document = JsonDocument.Parse(buffer.ToArray());
        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose();
        throw new ChatApiException(400, "Expected a JSON object.");
    }
    catch (JsonException) { throw new ChatApiException(400, "Invalid JSON request."); }
}

public partial class Program;
