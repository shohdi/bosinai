using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bonsai.Chat.Inference;

/// <summary>
/// Runs the custom Prism runtime required by the existing PTQ1_0 Bonsai GGUF.
/// The web app owns this hidden native process; Python and an external server are not needed.
/// </summary>
public sealed class BonsaiChatModel : IChatModel, IAsyncDisposable
{
    private const string Name = "Ternary Bonsai 2 · 27B";
    private readonly string _modelPath;
    private readonly string _runtimePath;
    private readonly string _stateDirectory;
    private readonly ModelCatalogOptions _options;
    private readonly string _systemPrompt;
    private readonly int _safetyMargin;
    private readonly ILogger<BonsaiChatModel> _logger;
    private readonly Queue<string> _recentErrors = new();
    private Process? _process;
    private HttpClient? _client;
    private string? _keyPath;
    private ModelStatus _status = new("available", Name, "Local model available; loads on the first message", true);
    private bool _disposed;

    public BonsaiChatModel(string modelPath, string runtimePath, string stateDirectory,
        ModelCatalogOptions options, string systemPrompt, int safetyMargin, ILogger<BonsaiChatModel> logger)
    {
        _modelPath = modelPath;
        _runtimePath = runtimePath;
        _stateDirectory = stateDirectory;
        _options = options;
        _systemPrompt = systemPrompt;
        _safetyMargin = safetyMargin;
        _logger = logger;
        if (_options.BonsaiContextSize < 512 || _options.BonsaiMaxTokens <= 0 ||
            _options.BonsaiMaxTokens + _safetyMargin >= _options.BonsaiContextSize)
            throw new ArgumentException("Bonsai context must exceed its maximum reply plus the safety margin.");
        if (_options.BonsaiCacheTypeK is not ("f16" or "q8_0" or "q4_0") ||
            _options.BonsaiCacheTypeV is not ("f16" or "q8_0" or "q4_0"))
            throw new ArgumentException("Bonsai cache types must be f16, q8_0 or q4_0.");
        if (_options.BonsaiBatchSize < 1 || _options.BonsaiUBatchSize < 1 ||
            _options.BonsaiUBatchSize > _options.BonsaiBatchSize)
            throw new ArgumentException("Bonsai batch sizes must be positive and micro-batch must not exceed batch size.");
    }

    public ModelStatus GetStatus()
    {
        if (!File.Exists(_modelPath)) return new("missing", Name, "The Bonsai GGUF file was not found.", false);
        if (!File.Exists(_runtimePath)) return new("missing", Name, "The Bonsai native runtime was not found.", false);
        if (_process is { HasExited: true })
            return new("error", Name, "The Bonsai runtime stopped. Retry your message to reload it.", false);
        return Volatile.Read(ref _status);
    }

    public async Task<string> PrepareInputAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var client = await EnsureStartedAsync(cancellationToken);
        return await InputTailLimiter.KeepTailAsync(content, (int)(_options.BonsaiContextSize / 2),
            async (text, token) =>
            {
                using var response = await client.PostAsJsonAsync("tokenize",
                    new { content = text, add_special = false, parse_special = true }, token);
                await EnsureSuccessAsync(response, token);
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token),
                    cancellationToken: token);
                return document.RootElement.GetProperty("tokens").GetArrayLength();
            }, cancellationToken);
    }

    public async IAsyncEnumerable<string> GenerateAsync(IReadOnlyList<InferenceMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (messages.Count == 0 || messages[^1].Role != "user" ||
            messages.Any(message => message.Role is not ("user" or "assistant") || message.Content is null))
            throw new ArgumentException("Chat messages must contain user/assistant text and end with a user message.");
        var client = await EnsureStartedAsync(cancellationToken);
        var history = new List<object>();
        if (!string.IsNullOrWhiteSpace(_systemPrompt)) history.Add(new { role = "system", content = _systemPrompt });
        history.AddRange(messages.Select(message => (object)new { role = message.Role, content = message.Content }));
        var templateSettings = new { enable_thinking = false };

        using var templateResponse = await client.PostAsJsonAsync("apply-template",
            new { messages = history, chat_template_kwargs = templateSettings }, cancellationToken);
        await EnsureSuccessAsync(templateResponse, cancellationToken);
        using var template = await JsonDocument.ParseAsync(await templateResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var prompt = template.RootElement.GetProperty("prompt").GetString();
        using var tokenResponse = await client.PostAsJsonAsync("tokenize",
            new { content = prompt, add_special = true }, cancellationToken);
        await EnsureSuccessAsync(tokenResponse, cancellationToken);
        using var tokenDocument = await JsonDocument.ParseAsync(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var inputTokens = tokenDocument.RootElement.GetProperty("tokens").GetArrayLength();
        var inputBudget = (int)_options.BonsaiContextSize - _options.BonsaiMaxTokens - _safetyMargin;
        if (inputTokens > inputBudget)
            throw new InvalidOperationException($"Context full: this conversation needs {inputTokens:N0} input tokens, " +
                $"but the safe input limit is {inputBudget:N0}. Start a new chat. Your history has been kept intact.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = "bonsai-local", messages = history, stream = true,
                temperature = 0.7, top_p = 0.8, top_k = 20, min_p = 0,
                max_tokens = _options.BonsaiMaxTokens, cache_prompt = false,
                chat_template_kwargs = templateSettings,
            }),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        var finished = false;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload == "[DONE]") yield break;
            if (payload.Length == 0) continue;
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException("Bonsai generation failed: " + error);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;
            if (choices[0].TryGetProperty("finish_reason", out var finishReason) &&
                finishReason.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(finishReason.GetString()))
                finished = true;
            if (choices[0].TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String &&
                content.GetString() is { Length: > 0 } text)
                yield return text;
        }
        if (!finished)
            throw new InvalidOperationException("The Bonsai response ended before completion. Retry your message; your saved history has been kept intact.");
    }

    public async IAsyncEnumerable<ModelDelta> CompleteAsync(ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ModelToolProtocol.ValidateRequest(request);
        var client = await EnsureStartedAsync(cancellationToken);
        var history = ModelToolProtocol.NormalizeSystemMessages(request.Messages).Select(message =>
        {
            var item = new Dictionary<string, object?> { ["role"] = message.Role, ["content"] = message.Content };
            if (message.ToolCallId is not null) item["tool_call_id"] = message.ToolCallId;
            if (message.ToolCalls is { Count: > 0 })
                item["tool_calls"] = message.ToolCalls.Select(call => new
                {
                    id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments },
                }).ToArray();
            return item;
        }).ToArray();
        var maxTokens = Math.Min(request.MaxTokens ?? _options.BonsaiMaxTokens, _options.BonsaiMaxTokens);
        var body = new Dictionary<string, object?>
        {
            ["model"] = "bonsai-local", ["messages"] = history, ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
            ["temperature"] = request.Temperature ?? 0.7f, ["top_p"] = 0.8, ["top_k"] = 20, ["min_p"] = 0,
            ["max_tokens"] = maxTokens, ["cache_prompt"] = false,
            ["chat_template_kwargs"] = new { enable_thinking = false },
        };
        if (request.Tools is { Count: > 0 })
        {
            body["tools"] = request.Tools.Select(tool => new
            {
                type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.Parameters },
            }).ToArray();
            body["tool_choice"] = request.ForcedToolName is { } forced
                ? (object)new { type = "function", function = new { name = forced } } : request.ToolChoice;
        }
        // Apply the same native template, including tool definitions, before checking the full context budget.
        using var templateResponse = await client.PostAsJsonAsync("apply-template", body, cancellationToken);
        await EnsureSuccessAsync(templateResponse, cancellationToken);
        using var template = await JsonDocument.ParseAsync(await templateResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var prompt = template.RootElement.GetProperty("prompt").GetString();
        using var tokenResponse = await client.PostAsJsonAsync("tokenize",
            new { content = prompt, add_special = true }, cancellationToken);
        await EnsureSuccessAsync(tokenResponse, cancellationToken);
        using var tokenDocument = await JsonDocument.ParseAsync(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var inputTokens = tokenDocument.RootElement.GetProperty("tokens").GetArrayLength();
        var inputBudget = (int)_options.BonsaiContextSize - maxTokens - _safetyMargin;
        if (inputTokens > inputBudget)
            throw new InvalidOperationException($"Context full: this request needs {inputTokens:N0} input tokens, " +
                $"but the safe input limit is {inputBudget:N0}. Reduce the request or compact the client history.");

        using var nativeRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        using var response = await client.SendAsync(nativeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        string? finishReason = null;
        int? outputTokens = null;
        var toolCalls = new Dictionary<int, (StringBuilder Name, StringBuilder Arguments)>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload == "[DONE]") break;
            if (payload.Length == 0) continue;
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException("Bonsai generation failed: " + error);
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var input) && input.TryGetInt32(out var inputCount)) inputTokens = inputCount;
                if (usage.TryGetProperty("completion_tokens", out var output) && output.TryGetInt32(out var outputCount)) outputTokens = outputCount;
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                finishReason = finish.GetString();
            if (!choice.TryGetProperty("delta", out var delta)) continue;
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String &&
                content.GetString() is { Length: > 0 } text)
                yield return new ModelDelta(Text: text);
            if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array) continue;
            foreach (var call in calls.EnumerateArray())
            {
                var index = call.TryGetProperty("index", out var indexValue) ? indexValue.GetInt32() : 0;
                var id = call.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
                string? name = null;
                string? arguments = null;
                if (call.TryGetProperty("function", out var function))
                {
                    if (function.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String) name = nameValue.GetString();
                    if (function.TryGetProperty("arguments", out var argumentValue) && argumentValue.ValueKind == JsonValueKind.String) arguments = argumentValue.GetString();
                }
                if (!toolCalls.TryGetValue(index, out var accumulated))
                    toolCalls[index] = accumulated = (new StringBuilder(), new StringBuilder());
                accumulated.Name.Append(name);
                accumulated.Arguments.Append(arguments);
                yield return new ModelDelta(ToolIndex: index, ToolCallId: id, ToolName: name, Arguments: arguments);
            }
        }
        if (string.IsNullOrEmpty(finishReason))
            throw new InvalidOperationException("The Bonsai response ended before completion. Retry the request.");
        if (toolCalls.Count > 0 && request.ToolChoice == "none")
            throw new InvalidOperationException("The local model returned a tool call when tools were disabled.");
        if (request.Tools is { Count: > 0 } && request.ToolChoice != "none")
        {
            var completeCalls = JsonSerializer.Serialize(new
            {
                tool_calls = toolCalls.OrderBy(pair => pair.Key).Select(pair => new
                {
                    name = pair.Value.Name.ToString(), arguments = pair.Value.Arguments.ToString(),
                }),
            });
            // Validate names, argument JSON, required parameters, and forced choices before announcing completion.
            ModelToolProtocol.ParseResponse(completeCalls, request);
        }
        else if (toolCalls.Count > 0)
            throw new InvalidOperationException("The local model returned a tool call without supplied tool definitions.");
        if (toolCalls.Count > 0 && finishReason == "stop") finishReason = "tool_calls";
        yield return new ModelDelta(FinishReason: finishReason, InputTokens: inputTokens, OutputTokens: outputTokens);
    }

    private async Task<HttpClient> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _client is not null) return _client;
        await StopProcessAsync();
        var status = GetStatus();
        if (!status.IsReady && status.State == "missing") throw new InvalidOperationException(status.Message);
        Volatile.Write(ref _status, new("loading", Name, "Loading local Bonsai model…", false));
        try
        {
            lock (_recentErrors) _recentErrors.Clear();
            Directory.CreateDirectory(_stateDirectory);
            _keyPath = Path.Combine(_stateDirectory, "bonsai-runtime-" + Guid.NewGuid().ToString("N") + ".key");
            var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await File.WriteAllTextAsync(_keyPath, apiKey, cancellationToken);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var start = new ProcessStartInfo(_runtimePath)
            {
                WorkingDirectory = Path.GetDirectoryName(_runtimePath)!,
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            string[] arguments = ["-m", _modelPath, "--alias", "bonsai-local", "--host", "127.0.0.1", "--port", port.ToString(),
                "--api-key-file", _keyPath, "--no-slots", "-ngl", _options.BonsaiGpuLayerCount.ToString(), "-fa", "on",
                "-c", _options.BonsaiContextSize.ToString(), "-np", "1", "-b", _options.BonsaiBatchSize.ToString(),
                "-ub", _options.BonsaiUBatchSize.ToString(), "--jinja",
                "--reasoning", "off", "--reasoning-budget", "0"];
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--cache-type-k");
            start.ArgumentList.Add(_options.BonsaiCacheTypeK);
            start.ArgumentList.Add("--cache-type-v");
            start.ArgumentList.Add(_options.BonsaiCacheTypeV);
            if (!_options.BonsaiKvOffload) start.ArgumentList.Add("--no-kv-offload");
            var process = new Process { StartInfo = start };
            process.ErrorDataReceived += (_, args) => RecordRuntimeLine(args.Data);
            process.OutputDataReceived += (_, args) => RecordRuntimeLine(args.Data);
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Could not start the native Bonsai runtime.");
            }
            catch { process.Dispose(); throw; }
            _process = process;
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();
            _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout.InfiniteTimeSpan };
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var timeout = DateTime.UtcNow.AddSeconds(Math.Max(10, _options.BonsaiStartupTimeoutSeconds));
            while (DateTime.UtcNow < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                {
                    string details;
                    lock (_recentErrors) details = string.Join(" ", _recentErrors.TakeLast(4));
                    throw new InvalidOperationException("The Bonsai runtime exited while loading. " + details);
                }
                try
                {
                    using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    healthTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    using var health = await _client.GetAsync("health", healthTimeout.Token);
                    if (health.IsSuccessStatusCode)
                    {
                        Volatile.Write(ref _status, new("ready", Name, "Local Bonsai model ready", true));
                        return _client;
                    }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                await Task.Delay(400, cancellationToken);
            }
            throw new InvalidOperationException("The Bonsai model took too long to load. Check available memory and the native runtime logs.");
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync();
            Volatile.Write(ref _status, new("available", Name, "Local model available; loads on the next message", true));
            throw;
        }
        catch (Exception error)
        {
            await StopProcessAsync();
            Volatile.Write(ref _status, new("error", Name, error.Message, false));
            throw new InvalidOperationException("Could not start Bonsai. " + error.Message, error);
        }
    }

    private void RecordRuntimeLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_recentErrors)
        {
            _recentErrors.Enqueue(line);
            while (_recentErrors.Count > 20) _recentErrors.Dequeue();
        }
        _logger.LogDebug("Bonsai runtime: {Line}", line);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"The Bonsai runtime returned HTTP {(int)response.StatusCode}: " +
            detail[..Math.Min(detail.Length, 500)]);
    }

    private async Task StopProcessAsync()
    {
        _client?.Dispose();
        _client = null;
        try
        {
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
                finally { _process.Dispose(); _process = null; }
            }
        }
        finally
        {
            if (_keyPath is not null)
            {
                try { if (File.Exists(_keyPath)) File.Delete(_keyPath); }
                finally { _keyPath = null; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopProcessAsync();
    }
}
