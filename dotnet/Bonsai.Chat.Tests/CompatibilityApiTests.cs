using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bonsai.Chat.Inference;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Bonsai.Chat.Tests;

public sealed class CompatibilityApiTests
{
    [Fact]
    public async Task ModelDiscoveryExposesSelectedAliasAndRealContextWithoutLoadingWeights()
    {
        using var directory = new CompatibilityDirectory();
        using var app = new CompatibilityApplication(directory.Path, new CompatibilityModel());
        using var client = app.CreateLocalClient();
        var body = await GetJson(client, "/v1/models");
        Assert.Equal("list", body.GetProperty("object").GetString());
        var models = body.GetProperty("data").EnumerateArray().ToArray();
        Assert.Contains(models, item => item.GetProperty("id").GetString() == "local");
        Assert.Contains(models, item => item.GetProperty("id").GetString() == "deepseek");
        Assert.DoesNotContain(models, item => item.GetProperty("id").GetString() == "bonsai");
        Assert.Equal(57344, models.Single(item => item.GetProperty("id").GetString() == "local")
            .GetProperty("context_window").GetInt32());
    }

    [Fact]
    public async Task ChatCompletionPreservesInstructionsToolsAndGenerationOverrides()
    {
        using var directory = new CompatibilityDirectory();
        var model = new CompatibilityModel();
        using var app = new CompatibilityApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "local", max_tokens = 321, temperature = 0.4,
            messages = new[] { new { role = "system", content = "Caller instructions" }, new { role = "user", content = "Code please" } },
            tools = new[] { ChatTool("read_file") }, tool_choice = "none"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("chat.completion", body.GetProperty("object").GetString());
        Assert.Equal("Hello local model.", body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", body.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        var request = Assert.Single(model.Requests);
        Assert.Contains(request.Messages, message => message.Role == "system" && message.Content == "Caller instructions");
        Assert.Equal(321, request.MaxTokens);
        Assert.Equal(0.4f, request.Temperature);
        Assert.Equal("none", request.ToolChoice);
        Assert.Equal("read_file", Assert.Single(request.Tools!).Name);
        Assert.Equal(0, model.PrepareCalls); // Client history is never silently suffix-trimmed.
    }

    [Fact]
    public async Task ChatStreamContainsTextFinishUsageAndDoneMarker()
    {
        using var directory = new CompatibilityDirectory();
        using var app = new CompatibilityApplication(directory.Path, new CompatibilityModel());
        using var client = app.CreateLocalClient();
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "local", stream = true, stream_options = new { include_usage = true },
            messages = new[] { new { role = "user", content = "Hello" } }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.EndsWith("data: [DONE]\n\n", raw.Replace("\r\n", "\n"));
        var events = ParseEvents(raw);
        Assert.All(events, item => Assert.Equal("chat.completion.chunk", item.Data.GetProperty("object").GetString()));
        var text = string.Concat(events.SelectMany(item => item.Data.GetProperty("choices").EnumerateArray())
            .Select(choice => choice.GetProperty("delta").TryGetProperty("content", out var value) ? value.GetString() : null));
        Assert.Equal("Hello local model.", text);
        Assert.Contains(events.SelectMany(item => item.Data.GetProperty("choices").EnumerateArray()),
            choice => choice.GetProperty("finish_reason").GetString() == "stop");
        Assert.Contains(events, item => item.Data.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object && usage.GetProperty("completion_tokens").GetInt32() == 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCompletionReturnsFunctionArgumentsAndAcceptsToolResults(bool stream)
    {
        using var directory = new CompatibilityDirectory();
        var model = new CompatibilityModel { Reply = request => request.Messages.Any(message => message.Role == "tool")
            ? CompatibilityModel.TextReply() : CompatibilityModel.ToolReply("read_file", "{\"path\":\"notes.txt\"}") };
        using var app = new CompatibilityApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "local", stream, messages = new[] { new { role = "user", content = "Read notes" } }, tools = new[] { ChatTool("read_file") }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (stream)
        {
            var chunks = ParseEvents(await response.Content.ReadAsStringAsync());
            var calls = chunks.SelectMany(item => item.Data.GetProperty("choices").EnumerateArray())
                .Where(choice => choice.GetProperty("delta").TryGetProperty("tool_calls", out _))
                .SelectMany(choice => choice.GetProperty("delta").GetProperty("tool_calls").EnumerateArray()).ToArray();
            Assert.Equal("call_test", calls.First().GetProperty("id").GetString());
            Assert.Equal("read_file", calls.First().GetProperty("function").GetProperty("name").GetString());
            Assert.Equal("{\"path\":\"notes.txt\"}", string.Concat(calls.Select(call =>
                call.GetProperty("function").TryGetProperty("arguments", out var arguments) ? arguments.GetString() : null)));
        }
        else
        {
            var body = await ReadJson(response);
            var choice = body.GetProperty("choices")[0];
            Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
            Assert.Equal("{\"path\":\"notes.txt\"}", choice.GetProperty("message").GetProperty("tool_calls")[0]
                .GetProperty("function").GetProperty("arguments").GetString());
        }
        using var followup = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "local", messages = new object[]
            {
                new { role = "user", content = "Read notes" },
                new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call_test", type = "function", function = new { name = "read_file", arguments = "{\"path\":\"notes.txt\"}" } } } },
                new { role = "tool", tool_call_id = "call_test", content = "File contents" }
            }, tools = new[] { ChatTool("read_file") }
        });
        Assert.Equal(HttpStatusCode.OK, followup.StatusCode);
        Assert.Equal("Hello local model.", (await ReadJson(followup)).GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        var request = model.Requests.Last();
        Assert.Contains(request.Messages, message => message.Role == "tool" && message.ToolCallId == "call_test" && message.Content == "File contents");
        Assert.Contains(request.Messages, message => message.Role == "assistant" && message.ToolCalls?.Single().Name == "read_file");
    }

    [Fact]
    public async Task ResponsesStreamHasOrderedLifecycleAndCompletedText()
    {
        using var directory = new CompatibilityDirectory();
        var model = new CompatibilityModel();
        using var app = new CompatibilityApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        using var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "local", stream = true, store = false, instructions = "Caller policy", input = "Hello", max_output_tokens = 123
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = ParseEvents(await response.Content.ReadAsStringAsync());
        var types = events.Select(item => item.Data.GetProperty("type").GetString()).ToArray();
        Assert.Equal("response.created", types.First());
        Assert.Equal("response.completed", types.Last());
        foreach (var type in new[] { "response.in_progress", "response.output_item.added", "response.content_part.added",
            "response.output_text.delta", "response.output_text.done", "response.content_part.done", "response.output_item.done" })
            Assert.Contains(type, types);
        Assert.All(events, item => Assert.Equal(item.Event, item.Data.GetProperty("type").GetString()));
        var sequences = events.Select(item => item.Data.GetProperty("sequence_number").GetInt32()).ToArray();
        Assert.Equal(sequences.Order(), sequences);
        Assert.Equal(sequences.Length, sequences.Distinct().Count());
        Assert.Equal("Hello local model.", string.Concat(events.Where(item => item.Event == "response.output_text.delta")
            .Select(item => item.Data.GetProperty("delta").GetString())));
        var completed = events.Last().Data.GetProperty("response");
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal("Hello local model.", completed.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(123, Assert.Single(model.Requests).MaxTokens);
        Assert.Contains(model.Requests.Single().Messages, message => message.Content == "Caller policy" && message.Role is "system" or "developer");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponsesFunctionsPreserveCallsAndAcceptResultContinuation(bool stream)
    {
        using var directory = new CompatibilityDirectory();
        var model = new CompatibilityModel { Reply = request => request.Messages.Any(message => message.Role == "tool")
            ? CompatibilityModel.TextReply() : CompatibilityModel.ToolReply("read_file", "{\"path\":\"notes.txt\"}") };
        using var app = new CompatibilityApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        using var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "local", stream, input = "Read notes", tools = new[] { ResponseTool("read_file") }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body;
        if (stream)
        {
            var events = ParseEvents(await response.Content.ReadAsStringAsync());
            Assert.Contains(events, item => item.Event == "response.function_call_arguments.delta");
            Assert.Contains(events, item => item.Event == "response.function_call_arguments.done"
                && item.Data.GetProperty("arguments").GetString() == "{\"path\":\"notes.txt\"}");
            body = events.Last().Data.GetProperty("response");
        }
        else body = await ReadJson(response);
        var call = body.GetProperty("output")[0];
        Assert.Equal("function_call", call.GetProperty("type").GetString());
        Assert.Equal("call_test", call.GetProperty("call_id").GetString());
        Assert.Equal("read_file", call.GetProperty("name").GetString());
        using var continuation = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "local", previous_response_id = body.GetProperty("id").GetString(),
            input = new[] { new { type = "function_call_output", call_id = "call_test", output = "File contents" } },
            tools = new[] { ResponseTool("read_file") }
        });
        Assert.Equal(HttpStatusCode.OK, continuation.StatusCode);
        Assert.Equal("Hello local model.", (await ReadJson(continuation)).GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Contains(model.Requests.Last().Messages, message => message.Role == "tool" && message.Content == "File contents" && message.ToolCallId == "call_test");
        Assert.Contains(model.Requests.Last().Messages, message => message.ToolCalls?.SingleOrDefault()?.Name == "read_file");
    }

    [Fact]
    public async Task ResponsesCustomToolsUnwrapRawInputAndRestoreNamespace()
    {
        const string patch = "*** Begin Patch\n*** End Patch";
        using var directory = new CompatibilityDirectory();
        var model = new CompatibilityModel
        {
            Reply = request => CompatibilityModel.ToolReply(request.Tools!.Single().Name, JsonSerializer.Serialize(new { input = patch }))
        };
        using var app = new CompatibilityApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        using var response = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "local", stream = true, input = "Apply patch", tools = new object[]
            {
                new { type = "namespace", name = "functions", description = "Editor tools", tools = new[]
                { new { type = "custom", name = "apply_patch", description = "Apply a patch", format = new { type = "text" } } } }
            }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = ParseEvents(await response.Content.ReadAsStringAsync());
        Assert.Contains(events, item => item.Event == "response.custom_tool_call_input.delta" && item.Data.GetProperty("delta").GetString() == patch);
        Assert.Contains(events, item => item.Event == "response.custom_tool_call_input.done" && item.Data.GetProperty("input").GetString() == patch);
        var call = events.Last().Data.GetProperty("response").GetProperty("output")[0];
        Assert.Equal("custom_tool_call", call.GetProperty("type").GetString());
        Assert.Equal("functions", call.GetProperty("namespace").GetString());
        Assert.Equal("apply_patch", call.GetProperty("name").GetString());
        Assert.Equal(patch, call.GetProperty("input").GetString());
        Assert.Equal("input", Assert.Single(model.Requests).Tools!.Single().Parameters.GetProperty("required")[0].GetString());
    }

    [Theory]
    [InlineData("/v1/chat/completions", "{\"model\":\"local\",\"messages\":[]}")]
    [InlineData("/v1/chat/completions", "{\"model\":\"local\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"max_tokens\":0}")]
    [InlineData("/v1/responses", "{\"model\":\"local\",\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_image\",\"image_url\":\"data:example\"}]}]}")]
    [InlineData("/v1/responses", "{\"model\":\"local\",\"input\":\"hello\",\"tools\":[{\"type\":\"web_search\"}]}")]
    [InlineData("/v1/responses", "{\"model\":\"local\",\"input\":\"hello\",\"stream\":\"yes\"}")]
    [InlineData("/v1/responses", "{bad json")]
    public async Task InvalidOrUnsupportedRequestsHaveStandardErrorsAndDoNotInvokeModel(string path, string json)
    {
        using var directory = new CompatibilityDirectory();
        var model = new CompatibilityModel();
        using var app = new CompatibilityApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        using var response = await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal(JsonValueKind.Object, error.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task UnselectedModelIsRejectedWithoutSwitchingOrLoadingIt()
    {
        using var directory = new CompatibilityDirectory();
        var model = new CompatibilityModel();
        using var app = new CompatibilityApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        using var response = await client.PostAsJsonAsync("/v1/responses", new { model = "bonsai", input = "Hello" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("model_not_selected", (await ReadJson(response)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("deepseek", app.Services.GetRequiredService<ModelCatalog>().SelectedId);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task ApiAndWebShareOneInferenceSlotAndApiHistoryDoesNotLeakIntoWeb()
    {
        using var directory = new CompatibilityDirectory();
        var model = new CompatibilityModel { BlockApi = true };
        using var app = new CompatibilityApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        using var sessionResponse = await client.PostAsJsonAsync("/api/sessions", new { title = "Web chat" });
        var id = (await ReadJson(sessionResponse)).GetProperty("session").GetProperty("id").GetString();
        var api = client.PostAsJsonAsync("/v1/responses", new { model = "local", input = "Private API conversation" });
        try
        {
            await model.ApiStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var blockedSelection = await client.PostAsJsonAsync("/api/models/select", new { id = "bonsai" });
            Assert.Equal(HttpStatusCode.Conflict, blockedSelection.StatusCode);
            using var sent = await client.PostAsJsonAsync($"/api/sessions/{id}/messages", new { content = "Web conversation" });
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
            var jobId = (await ReadJson(sent)).GetProperty("job").GetProperty("id").GetString();
            Assert.Empty(model.WebHistories);
            model.ReleaseApi.TrySetResult();
            using var apiResponse = await api.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);
            await WaitUntil(async () => (await GetJson(client, $"/api/jobs/{jobId}")).GetProperty("job").GetProperty("status").GetString() == "completed");
            Assert.Equal(1, model.MaximumActive);
            Assert.Equal("Web conversation", Assert.Single(model.WebHistories).Single().Content);
            var messages = (await GetJson(client, $"/api/sessions/{id}")).GetProperty("session").GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.DoesNotContain("Private API", messages.GetRawText());
            Assert.Single((await GetJson(client, "/api/sessions")).GetProperty("sessions").EnumerateArray());
        }
        finally { model.ReleaseApi.TrySetResult(); }
    }

    [Fact]
    public async Task CancellingApiRequestReleasesSharedSlotForNextRequest()
    {
        using var directory = new CompatibilityDirectory();
        var model = new CompatibilityModel { BlockApi = true };
        using var app = new CompatibilityApplication(directory.Path, model);
        using var client = app.CreateLocalClient();
        using var cancellation = new CancellationTokenSource();
        var pending = client.PostAsJsonAsync("/v1/responses", new { model = "local", input = "Cancel me" }, cancellation.Token);
        await model.ApiStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await WaitUntil(() => Task.FromResult(model.Active == 0));
        model.BlockApi = false;
        using var retry = await client.PostAsJsonAsync("/v1/responses", new { model = "local", input = "Retry" });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1, model.MaximumActive);
    }

    [Theory]
    [InlineData("/v1/responses")]
    [InlineData("/v1/chat/completions")]
    public async Task NativeClientsAreAllowedAndForeignWebOriginsStayBlocked(string path)
    {
        using var directory = new CompatibilityDirectory();
        using var app = new CompatibilityApplication(directory.Path, new CompatibilityModel());
        using var client = app.CreateLocalClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(path.EndsWith("responses") ? (object)new { model = "local", input = "Hello" }
                : new { model = "local", messages = new[] { new { role = "user", content = "Hello" } } })
        };
        request.Headers.Add("Origin", "https://untrusted.example");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var models = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        Assert.Contains("no-store", models.Headers.CacheControl?.ToString());
    }

    internal static object ChatTool(string name) => new { type = "function", function = new { name, description = "Read a file", parameters = FileSchema() } };
    internal static object ResponseTool(string name) => new { type = "function", name, description = "Read a file", parameters = FileSchema() };
    private static object FileSchema() => new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" }, additionalProperties = false };

    internal static List<(string Event, JsonElement Data)> ParseEvents(string text)
    {
        var result = new List<(string, JsonElement)>();
        foreach (var frame in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = frame.Split('\n');
            var data = string.Join("\n", lines.Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..].TrimStart()));
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") continue;
            using var document = JsonDocument.Parse(data);
            result.Add((lines.FirstOrDefault(line => line.StartsWith("event:", StringComparison.Ordinal))?[6..].Trim() ?? "", document.RootElement.Clone()));
        }
        return result;
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!await condition()) await Task.Delay(10, timeout.Token);
    }
    private static async Task<JsonElement> GetJson(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJson(response);
    }
    internal static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

internal sealed class CompatibilityModel : IChatModel
{
    private int _active, _maximumActive, _prepareCalls;
    public int Active => Volatile.Read(ref _active);
    public int MaximumActive => Volatile.Read(ref _maximumActive);
    public int PrepareCalls => Volatile.Read(ref _prepareCalls);
    public bool BlockApi { get; set; }
    public TaskCompletionSource ApiStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseApi { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<ModelRequest> Requests { get; } = [];
    public List<InferenceMessage[]> WebHistories { get; } = [];
    public Func<ModelRequest, IEnumerable<ModelDelta>> Reply { get; set; } = _ => TextReply();
    public ModelStatus GetStatus() => new("ready", "Protocol fake", "Ready", true);
    public Task<string> PrepareInputAsync(string content, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _prepareCalls);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(content);
    }
    public async IAsyncEnumerable<ModelDelta> CompleteAsync(ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            lock (Requests) Requests.Add(request);
            ApiStarted.TrySetResult();
            if (BlockApi) await ReleaseApi.Task.WaitAsync(cancellationToken);
            foreach (var delta in Reply(request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return delta;
            }
        }
        finally { Interlocked.Decrement(ref _active); }
    }
    public async IAsyncEnumerable<string> GenerateAsync(IReadOnlyList<InferenceMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            lock (WebHistories) WebHistories.Add(messages.ToArray());
            await Task.Delay(10, cancellationToken);
            yield return "Web reply";
        }
        finally { Interlocked.Decrement(ref _active); }
    }
    private void Enter()
    {
        var count = Interlocked.Increment(ref _active);
        int previous;
        do { previous = Volatile.Read(ref _maximumActive); if (previous >= count) break; }
        while (Interlocked.CompareExchange(ref _maximumActive, count, previous) != previous);
    }
    public static IEnumerable<ModelDelta> TextReply()
    {
        yield return new ModelDelta(Text: "Hello ");
        yield return new ModelDelta(Text: "local model.");
        yield return new ModelDelta(FinishReason: "stop", InputTokens: 8, OutputTokens: 4);
    }
    public static IEnumerable<ModelDelta> ToolReply(string name, string arguments)
    {
        yield return new ModelDelta(ToolCallId: "call_test", ToolName: name, Arguments: arguments[..(arguments.Length / 2)]);
        yield return new ModelDelta(Arguments: arguments[(arguments.Length / 2)..]);
        yield return new ModelDelta(FinishReason: "tool_calls", InputTokens: 8, OutputTokens: 4);
    }
}

internal sealed class CompatibilityApplication(string directory, CompatibilityModel model) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chat:DataDirectory"] = directory,
            ["Models:DefaultModelId"] = "deepseek",
            ["Models:SelectionFile"] = System.IO.Path.Combine(directory, "metadata", "selected-model.json"),
            ["Llama:ModelPath"] = System.IO.Path.Combine(directory, "missing-deepseek.gguf"),
            ["Models:BonsaiModelPath"] = System.IO.Path.Combine(directory, "missing-bonsai.gguf"),
            ["Models:BonsaiRuntimePath"] = System.IO.Path.Combine(directory, "missing-runtime")
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatModel>();
            services.AddSingleton<IChatModel>(model);
        });
    }
    public HttpClient CreateLocalClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false
    });
}

internal sealed class CompatibilityDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bonsai-compat-tests", Guid.NewGuid().ToString("N"));
    public CompatibilityDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        var fullPath = System.IO.Path.GetFullPath(Path);
        var parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bonsai-compat-tests"));
        if (!string.Equals(System.IO.Path.GetDirectoryName(fullPath), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its temporary directory.");
        Directory.Delete(fullPath, recursive: true);
    }
}
