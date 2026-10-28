using System.Text;
using System.Text.Json;
using Bonsai.Chat.Chat;
using Bonsai.Chat.Inference;
using Microsoft.AspNetCore.Http.Features;

namespace Bonsai.Chat.Api;

/// <summary>OpenAI HTTP protocols sharing the web application's one inference scheduler and loaded model.</summary>
public static class ApiEndpoints
{
    private const int MaxBodyBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapOpenAiApi(WebApplication app)
    {
        var states = new ResponseStateStore();
        app.MapGet("/v1/models", (ModelCatalog catalog) =>
        {
            var selected = catalog.SelectedId;
            var current = catalog.ListModels().First(model => model.Id == selected);
            object Model(string id) => new { id, @object = "model", created = 0, owned_by = "local",
                name = current.Name, context_length = current.ContextSize, context_window = current.ContextSize };
            return Results.Json(new { @object = "list", data = new[] { Model("local"), Model(selected) } });
        });
        app.MapPost("/v1/chat/completions", (HttpContext context, SharedInferenceService inference) =>
            HandleAsync(context, inference, states, responses: false, app.Logger));
        app.MapPost("/v1/responses", (HttpContext context, SharedInferenceService inference) =>
            HandleAsync(context, inference, states, responses: true, app.Logger));
        app.MapGet("/v1/responses", async (HttpContext context) =>
            await WriteErrorAsync(context, new OpenAiException(426, "WebSockets are not supported. Configure wire_api=responses and supports_websockets=false, then POST with stream=true.", "unsupported_transport")));
        app.MapGet("/v1/responses/{id}", async (HttpContext context, string id) =>
        {
            try { await context.Response.WriteAsJsonAsync(states.Get(id).Response, context.RequestAborted); }
            catch (OpenAiException error) { await WriteErrorAsync(context, error); }
        });
        app.MapFallback("/v1/{**path}", async (HttpContext context) =>
            await WriteErrorAsync(context, new OpenAiException(404, "API endpoint not found.", "not_found")));
    }

    private static async Task HandleAsync(HttpContext context, SharedInferenceService inference,
        ResponseStateStore states, bool responses, ILogger logger)
    {
        var streaming = false;
        try
        {
            using var document = await ReadBodyAsync(context);
            var request = OpenAiRequestParser.Parse(document.RootElement, responses);
            inference.ValidateModel(request.Model);
            if (request.PreviousResponseId is not null)
            {
                var previous = states.Get(request.PreviousResponseId);
                // Instructions are request scoped. Previous ordinary system/developer input stays in history.
                var instructions = document.RootElement.TryGetProperty("instructions", out var instruction)
                    && instruction.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(instruction.GetString());
                var fresh = request.Completion.Messages;
                var messages = instructions
                    ? new[] { fresh[0] }.Concat(previous.Messages).Concat(fresh.Skip(1)).ToArray()
                    : previous.Messages.Concat(fresh).ToArray();
                request = request with { Completion = request.Completion with { Messages = messages } };
            }
            var id = (responses ? "resp_" : "chatcmpl-") + Guid.NewGuid().ToString("N");
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var accumulator = new CompletionAccumulator(request.Tools);
            await using var iterator = inference.GenerateAsync(request.Model, request.Completion, context.RequestAborted)
                .GetAsyncEnumerator(context.RequestAborted);
            // Validate admission/model loading before committing SSE headers, so normal API errors retain their status.
            var hasDelta = await iterator.MoveNextAsync();
            if (request.Stream)
            {
                streaming = true;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache, no-store";
                context.Response.Headers["X-Accel-Buffering"] = "no";
                context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                if (responses)
                    await StreamResponsesAsync(context, request, states, id, created, accumulator, iterator, hasDelta);
                else await StreamChatAsync(context, request, id, created, accumulator, iterator, hasDelta);
            }
            else
            {
                while (hasDelta)
                {
                    accumulator.Add(iterator.Current);
                    hasDelta = await iterator.MoveNextAsync();
                }
                accumulator.Validate();
                if (responses)
                {
                    var output = accumulator.ResponseOutput();
                    var response = ResponseObject(request, id, created, accumulator, output);
                    Remember(states, request, id, response, accumulator);
                    await context.Response.WriteAsJsonAsync(response, Json, context.RequestAborted);
                }
                else await context.Response.WriteAsJsonAsync(ChatObject(request, id, created, accumulator), Json, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (context.RequestAborted.IsCancellationRequested) return;
            if (error is not (OpenAiException or ChatApiException or BadHttpRequestException or JsonException or ArgumentException or InvalidOperationException))
                logger.LogError(error, "Local model API request failed");
            if (streaming && context.Response.HasStarted)
            {
                var details = ErrorDetails(error);
                if (responses) await EventAsync(context, "error", new { type = "error", code = details.Code,
                    message = details.Message, param = details.Param, sequence_number = int.MaxValue });
                else await DataAsync(context, new { error = new { message = details.Message, type = details.Type, param = details.Param, code = details.Code } });
            }
            else await WriteErrorAsync(context, error);
        }
    }

    private static async Task StreamChatAsync(HttpContext context, OpenAiRequest request, string id, long created,
        CompletionAccumulator result, IAsyncEnumerator<ModelDelta> iterator, bool hasDelta)
    {
        object Chunk(object delta, object? finishReason = null, object? usage = null) => new
        {
            id, @object = "chat.completion.chunk", created, model = request.Model,
            choices = new[] { new { index = 0, delta, logprobs = (object?)null, finish_reason = finishReason } }, usage
        };
        await DataAsync(context, Chunk(new { role = "assistant", content = "" }));
        while (hasDelta)
        {
            var delta = iterator.Current;
            result.Add(delta);
            if (!string.IsNullOrEmpty(delta.Text)) await DataAsync(context, Chunk(new { content = delta.Text }));
            hasDelta = await iterator.MoveNextAsync();
        }
        result.Validate();
        foreach (var call in result.Calls.Values.OrderBy(call => call.Index))
            await DataAsync(context, Chunk(new { tool_calls = new[] { new { index = call.Index, id = call.Id,
                type = "function", function = new { name = call.Name, arguments = call.Arguments.ToString() } } } }));
        await DataAsync(context, Chunk(new { }, result.ChatFinishReason));
        if (result.Usage(chat: true) is { } usage)
            await DataAsync(context, new { id, @object = "chat.completion.chunk", created, model = request.Model, choices = Array.Empty<object>(), usage });
        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task StreamResponsesAsync(HttpContext context, OpenAiRequest request, ResponseStateStore states,
        string id, long created, CompletionAccumulator result, IAsyncEnumerator<ModelDelta> iterator, bool hasDelta)
    {
        var sequence = 0;
        Task Emit(string type, Dictionary<string, object?> fields)
        {
            fields["type"] = type;
            fields["sequence_number"] = sequence++;
            return EventAsync(context, type, fields);
        }
        await Emit("response.created", new() { ["response"] = ResponseObject(request, id, created, result, [], "in_progress") });
        await Emit("response.in_progress", new() { ["response"] = ResponseObject(request, id, created, result, [], "in_progress") });
        var textStarted = false;
        async Task StartText()
        {
            if (textStarted) return;
            textStarted = true;
            await Emit("response.output_item.added", new() { ["output_index"] = 0, ["item"] = result.TextItem(completed: false) });
            await Emit("response.content_part.added", new() { ["item_id"] = result.TextId, ["output_index"] = 0,
                ["content_index"] = 0, ["part"] = TextPart("") });
        }
        while (hasDelta)
        {
            var delta = iterator.Current;
            result.Add(delta);
            if (!string.IsNullOrEmpty(delta.Text))
            {
                await StartText();
                await Emit("response.output_text.delta", new() { ["item_id"] = result.TextId, ["output_index"] = 0,
                    ["content_index"] = 0, ["delta"] = delta.Text, ["logprobs"] = Array.Empty<object>() });
            }
            hasDelta = await iterator.MoveNextAsync();
        }
        result.Validate();
        if (!textStarted && result.Calls.Count == 0) await StartText();
        if (textStarted)
        {
            await Emit("response.output_text.done", new() { ["item_id"] = result.TextId, ["output_index"] = 0,
                ["content_index"] = 0, ["text"] = result.Text.ToString(), ["logprobs"] = Array.Empty<object>() });
            await Emit("response.content_part.done", new() { ["item_id"] = result.TextId, ["output_index"] = 0,
                ["content_index"] = 0, ["part"] = TextPart(result.Text.ToString()) });
            await Emit("response.output_item.done", new() { ["output_index"] = 0, ["item"] = result.TextItem(completed: true) });
        }
        var index = textStarted ? 1 : 0;
        foreach (var call in result.Calls.Values.OrderBy(call => call.Index))
        {
            var tool = result.Tool(call.Name);
            var item = result.CallItem(call, completed: false);
            var itemId = (string)item["id"]!;
            await Emit("response.output_item.added", new() { ["output_index"] = index, ["item"] = item });
            var argumentName = tool.Custom ? "input" : "arguments";
            var argument = tool.Custom ? result.CustomInput(call) : call.Arguments.ToString();
            var eventStem = tool.Custom ? "response.custom_tool_call_input" : "response.function_call_arguments";
            await Emit(eventStem + ".delta", new() { ["item_id"] = itemId, ["output_index"] = index, ["delta"] = argument });
            await Emit(eventStem + ".done", new() { ["item_id"] = itemId, ["output_index"] = index, [argumentName] = argument });
            await Emit("response.output_item.done", new() { ["output_index"] = index, ["item"] = result.CallItem(call, completed: true) });
            index++;
        }
        var response = ResponseObject(request, id, created, result, result.ResponseOutput());
        Remember(states, request, id, response, result);
        await Emit(result.FinishReason == "length" ? "response.incomplete" : "response.completed", new() { ["response"] = response });
    }

    private static object ChatObject(OpenAiRequest request, string id, long created, CompletionAccumulator result)
    {
        var message = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = result.Text.Length > 0 || result.Calls.Count == 0 ? result.Text.ToString() : null };
        if (result.Calls.Count > 0) message["tool_calls"] = result.Calls.Values.OrderBy(call => call.Index)
            .Select(call => new { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments.ToString() } }).ToArray();
        return new { id, @object = "chat.completion", created, model = request.Model,
            choices = new[] { new { index = 0, message, logprobs = (object?)null, finish_reason = result.ChatFinishReason } }, usage = result.Usage(chat: true) };
    }

    private static object ResponseObject(OpenAiRequest request, string id, long created, CompletionAccumulator result,
        IReadOnlyList<object> output, string? status = null) => new
    {
        id, @object = "response", created_at = created,
        status = status ?? (result.FinishReason == "length" ? "incomplete" : "completed"),
        error = (object?)null, incomplete_details = result.FinishReason == "length" && status is null ? new { reason = "max_output_tokens" } : null,
        instructions = (object?)null, max_output_tokens = request.Completion.MaxTokens, model = request.Model, output,
        parallel_tool_calls = true, previous_response_id = request.PreviousResponseId, reasoning = new { effort = (object?)null, summary = (object?)null },
        store = request.Store, temperature = request.Completion.Temperature, text = new { format = new { type = "text" } },
        tool_choice = request.Completion.ToolChoice, tools = Array.Empty<object>(), truncation = "disabled", usage = result.Usage(chat: false),
        metadata = new { }
    };

    private static void Remember(ResponseStateStore states, OpenAiRequest request, string id, object response, CompletionAccumulator result)
    {
        if (!request.Store) return;
        var messages = request.Completion.Messages.ToList();
        // Responses instructions do not carry forward with previous_response_id.
        // The parser puts explicit instructions first; ordinary input system messages are retained.
        if (request.HasInstructions) messages.RemoveAt(0);
        if (result.Text.Length > 0) messages.Add(new("assistant", result.Text.ToString()));
        if (result.Calls.Count > 0) messages.Add(new("assistant", null, ToolCalls: result.Calls.Values.OrderBy(call => call.Index)
            .Select(call => new ModelToolCall(call.Id, call.Name, call.Arguments.ToString(), call.Index)).ToArray()));
        states.Add(id, messages, response);
    }

    private static object TextPart(string text) => new { type = "output_text", text, annotations = Array.Empty<object>(), logprobs = Array.Empty<object>() };

    private static async Task<JsonDocument> ReadBodyAsync(HttpContext context)
    {
        if (!string.Equals(context.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            throw new OpenAiException(415, "Send an application/json request.", "unsupported_media_type");
        if (context.Request.ContentLength > MaxBodyBytes) throw new OpenAiException(413, "Request body is too large.", "request_too_large");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + count > MaxBodyBytes) throw new OpenAiException(413, "Request body is too large.", "request_too_large");
            buffer.Write(chunk, 0, count);
        }
        var result = JsonDocument.Parse(buffer.ToArray());
        if (result.RootElement.ValueKind == JsonValueKind.Object) return result;
        result.Dispose();
        throw new OpenAiException(400, "Expected a JSON object.");
    }

    private static async Task DataAsync(HttpContext context, object data)
    {
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(data, Json) + "\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task EventAsync(HttpContext context, string name, object data)
    {
        await context.Response.WriteAsync("event: " + name + "\ndata: " + JsonSerializer.Serialize(data, Json) + "\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private sealed record ErrorInfo(int Status, string Message, string Type, string Code, string? Param);

    private static ErrorInfo ErrorDetails(Exception error) => error switch
    {
        OpenAiException api => new(api.Status, api.Message, api.Status >= 500 ? "server_error" : "invalid_request_error", api.Code, api.Param),
        ChatApiException api => new(api.StatusCode, api.Message, api.StatusCode >= 500 ? "server_error" : api.StatusCode == 429 ? "rate_limit_error" : "invalid_request_error",
            api.StatusCode == 409 ? "model_not_selected" : api.StatusCode == 404 ? "model_not_found" : api.StatusCode == 429 ? "rate_limit_exceeded" : "invalid_request_error", null),
        BadHttpRequestException bad => new(bad.StatusCode, "Invalid request or request body is too large.", "invalid_request_error", "invalid_request_error", null),
        JsonException => new(400, "Invalid JSON request.", "invalid_request_error", "invalid_json", null),
        ArgumentException argument => new(400, argument.Message, "invalid_request_error", "invalid_request_error", null),
        InvalidOperationException invalid when invalid.Message.StartsWith("Context full", StringComparison.OrdinalIgnoreCase) =>
            new(400, invalid.Message, "invalid_request_error", "context_length_exceeded", null),
        InvalidOperationException invalid when invalid.Message.Contains("local model", StringComparison.OrdinalIgnoreCase)
            && (invalid.Message.Contains("tool", StringComparison.OrdinalIgnoreCase) || invalid.Message.Contains("argument", StringComparison.OrdinalIgnoreCase)) =>
            new(502, invalid.Message, "server_error", "invalid_model_output", null),
        InvalidOperationException => new(503, "The selected model could not complete this request. Check its status and the server logs.", "server_error", "model_error", null),
        _ => new(503, "The local model could not complete this request. Check the server logs and selected model status.", "server_error", "model_error", null)
    };

    private static async Task WriteErrorAsync(HttpContext context, Exception error)
    {
        var details = ErrorDetails(error);
        context.Response.StatusCode = details.Status;
        await context.Response.WriteAsJsonAsync(new { error = new { message = details.Message, type = details.Type,
            param = details.Param, code = details.Code } }, Json, context.RequestAborted);
    }

    private sealed class CompletionAccumulator(IReadOnlyDictionary<string, WireTool> tools)
    {
        public string TextId { get; } = "msg_" + Guid.NewGuid().ToString("N");
        public StringBuilder Text { get; } = new();
        public SortedDictionary<int, Call> Calls { get; } = new();
        public string? FinishReason { get; private set; }
        private int? _inputTokens;
        private int? _outputTokens;
        public string ChatFinishReason => FinishReason == "length" ? "length" : Calls.Count > 0 ? "tool_calls" : "stop";

        public sealed class Call(int index)
        {
            public int Index { get; } = index;
            public string Id { get; set; } = "call_" + Guid.NewGuid().ToString("N");
            public string ItemId { get; } = "fc_" + Guid.NewGuid().ToString("N");
            public string Name { get; set; } = "";
            public StringBuilder Arguments { get; } = new();
        }

        public void Add(ModelDelta delta)
        {
            if (delta.Text is not null) Text.Append(delta.Text);
            if (delta.ToolCallId is not null || delta.ToolName is not null || delta.Arguments is not null)
            {
                if (!Calls.TryGetValue(delta.ToolIndex, out var call)) Calls.Add(delta.ToolIndex, call = new(delta.ToolIndex));
                if (!string.IsNullOrEmpty(delta.ToolCallId)) call.Id = delta.ToolCallId;
                if (!string.IsNullOrEmpty(delta.ToolName)) call.Name += delta.ToolName;
                if (delta.Arguments is not null) call.Arguments.Append(delta.Arguments);
            }
            if (delta.FinishReason is not null) FinishReason = delta.FinishReason;
            _inputTokens = delta.InputTokens ?? _inputTokens;
            _outputTokens = delta.OutputTokens ?? _outputTokens;
        }

        public WireTool Tool(string name) => tools.TryGetValue(name, out var tool) ? tool
            : throw new OpenAiException(502, "The model emitted an undefined tool name.", "invalid_model_output");

        public void Validate()
        {
            foreach (var call in Calls.Values)
            {
                var tool = Tool(call.Name);
                try
                {
                    using var json = JsonDocument.Parse(call.Arguments.ToString());
                    if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    if (tool.Custom && (!json.RootElement.TryGetProperty("input", out var value) || value.ValueKind != JsonValueKind.String)) throw new JsonException();
                }
                catch (JsonException) { throw new OpenAiException(502, "The model emitted incomplete or invalid tool arguments. Retry or reduce the requested work.", "invalid_model_output"); }
            }
        }

        public string CustomInput(Call call)
        {
            using var json = JsonDocument.Parse(call.Arguments.ToString());
            return json.RootElement.GetProperty("input").GetString()!;
        }

        public Dictionary<string, object?> CallItem(Call call, bool completed)
        {
            var tool = Tool(call.Name);
            var item = new Dictionary<string, object?> { ["type"] = tool.Custom ? "custom_tool_call" : "function_call",
                ["id"] = call.ItemId, ["call_id"] = call.Id, ["name"] = tool.Name, ["status"] = completed ? "completed" : "in_progress" };
            if (tool.Namespace is not null) item["namespace"] = tool.Namespace;
            item[tool.Custom ? "input" : "arguments"] = completed ? (tool.Custom ? CustomInput(call) : call.Arguments.ToString()) : "";
            return item;
        }

        public object TextItem(bool completed) => new { type = "message", id = TextId,
            status = completed ? "completed" : "in_progress", role = "assistant", content = completed ? new[] { TextPart(Text.ToString()) } : Array.Empty<object>() };

        public IReadOnlyList<object> ResponseOutput()
        {
            var output = new List<object>();
            if (Text.Length > 0 || Calls.Count == 0) output.Add(TextItem(completed: true));
            output.AddRange(Calls.Values.OrderBy(call => call.Index).Select(call => CallItem(call, completed: true)));
            return output;
        }

        public object? Usage(bool chat)
        {
            if (_inputTokens is null || _outputTokens is null) return null;
            return chat ? new { prompt_tokens = _inputTokens, completion_tokens = _outputTokens, total_tokens = _inputTokens + _outputTokens } :
                new { input_tokens = _inputTokens, input_tokens_details = new { cached_tokens = 0 }, output_tokens = _outputTokens,
                    output_tokens_details = new { reasoning_tokens = 0 }, total_tokens = _inputTokens + _outputTokens };
        }
    }
}
