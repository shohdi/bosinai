using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;
using Microsoft.Extensions.Options;

namespace Bonsai.Chat.Inference;

/// <summary>
/// One lazily loaded GGUF model with one generation at a time. Each turn gets a fresh
/// native context, so a stopped or failed request cannot contaminate another conversation.
/// </summary>
public sealed class LlamaChatModel : IChatModel, IAsyncDisposable
{
    private readonly LlamaOptions _options;
    private readonly ILogger<LlamaChatModel> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _modelPath;
    private LLamaWeights? _weights;
    private ModelStatus _status;
    private bool _disposed;

    public LlamaChatModel(
        IOptions<LlamaOptions> options,
        IHostEnvironment environment,
        ILogger<LlamaChatModel> logger)
    {
        _options = options.Value;
        _logger = logger;
        Validator.ValidateObject(_options, new ValidationContext(_options), validateAllProperties: true);
        if (!float.IsFinite(_options.Temperature) || !float.IsFinite(_options.TopP))
            throw new ValidationException("Llama sampling options must be finite numbers.");
        if (_options.SafetyMarginTokens >= _options.ContextSize / 2)
            throw new ValidationException("Llama safety margin must leave room for a reply after half-window input.");
        if (_options.ChatTemplate is not ("DeepSeekCoderV2" or "Model"))
            throw new ValidationException("Llama ChatTemplate must be DeepSeekCoderV2 or Model.");
        if (_options.CacheTypeV != "F16" && _options.FlashAttention != true)
            throw new ValidationException("Quantized Llama CacheTypeV requires FlashAttention=true.");

        _modelPath = string.IsNullOrWhiteSpace(_options.ModelPath)
            ? ""
            : Path.GetFullPath(_options.ModelPath, environment.ContentRootPath);
        _status = AvailableStatus();
    }

    public ModelStatus GetStatus()
    {
        var status = Volatile.Read(ref _status);
        // A missing model can be supplied after startup without restarting the web app.
        return status.State is "not_configured" or "missing" or "available"
            ? AvailableStatus()
            : status;
    }

    public async Task<string> PrepareInputAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var weights = await LoadAsync(cancellationToken);
            return await InputTailLimiter.KeepTailAsync(content, (int)(_options.ContextSize / 2),
                (text, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(weights.Tokenize(text, add_bos: false, special: true, Encoding.UTF8).Length);
                }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<string> GenerateAsync(
        IReadOnlyList<InferenceMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0 || messages[^1].Role != "user")
            throw new ArgumentException("A chat turn must end with a user message.", nameof(messages));
        if (messages.Any(message => message.Role is not ("user" or "assistant") || message.Content is null))
            throw new ArgumentException("Chat messages must contain user or assistant text.", nameof(messages));

        var input = new List<ModelMessage>();
        if (!string.IsNullOrWhiteSpace(_options.SystemPrompt))
            input.Add(new("system", _options.SystemPrompt));
        input.AddRange(messages.Select(message => new ModelMessage(message.Role, message.Content)));
        await foreach (var delta in CompleteAsync(new ModelRequest(input, Temperature: _options.Temperature), cancellationToken))
            if (delta.Text is { Length: > 0 } text) yield return text;
    }

    public async IAsyncEnumerable<ModelDelta> CompleteAsync(ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ModelToolProtocol.ValidateRequest(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var weights = await LoadAsync(cancellationToken);
            // Count the actual rendered prompt, including instructions and tool definitions.
            // Agent instructions belong to this request; the web default is supplied only by GenerateAsync.
            request = await ModelContextPolicy.PrepareAsync(request, (int)_options.ContextSize,
                (candidate, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(TokenizeRequest(weights, candidate).Length);
                }, cancellationToken);
            var tokens = TokenizeRequest(weights, request);
            var maxTokens = ModelContextPolicy.RemainingOutputTokens(
                (int)_options.ContextSize, tokens.Length, _options.SafetyMarginTokens, request.MaxTokens);

            cancellationToken.ThrowIfCancellationRequested();
            using var context = weights.CreateContext(CreateParameters(), _logger);
            using var sampling = new DefaultSamplingPipeline
            {
                Temperature = request.Temperature ?? _options.Temperature,
                TopP = _options.TopP,
            };
            var batch = new LLamaBatch();
            var decoder = new StreamingTokenDecoder(context) { DecodeSpecialTokens = false };
            var batchSize = (int)context.BatchSize;
            for (var offset = 0; offset < tokens.Length; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch.Clear();
                var end = Math.Min(offset + batchSize, tokens.Length);
                for (var index = offset; index < end; index++)
                    batch.Add(tokens[index], index, LLamaSeqId.Zero, logits: index == tokens.Length - 1);
                await DecodeAsync(context, batch, cancellationToken);
            }
            var useTools = request.Tools is { Count: > 0 } && request.ToolChoice != "none";
            var buffered = new StringBuilder();
            var outputTokens = 0;
            var stopped = false;
            var position = tokens.Length;
            for (var generated = 0; generated < maxTokens; generated++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = sampling.Sample(context.NativeHandle, batch.TokenCount - 1);
                if (token.IsEndOfGeneration(weights.Vocab))
                {
                    stopped = true;
                    break;
                }
                outputTokens++;
                decoder.Add(token);
                var chunk = decoder.Read();
                if (chunk.Length > 0)
                {
                    if (useTools) buffered.Append(chunk);
                    else yield return new ModelDelta(Text: chunk);
                }
                if (generated + 1 == maxTokens) break;
                cancellationToken.ThrowIfCancellationRequested();
                batch.Clear();
                batch.Add(token, position++, LLamaSeqId.Zero, logits: true);
                await DecodeAsync(context, batch, cancellationToken);
            }
            if (useTools)
            {
                foreach (var delta in ModelToolProtocol.ParseResponse(buffered.ToString(), request))
                    yield return delta.FinishReason is null ? delta : delta with
                    {
                        FinishReason = delta.FinishReason == "stop" && !stopped ? "length" : delta.FinishReason,
                        InputTokens = tokens.Length, OutputTokens = outputTokens,
                    };
            }
            else yield return new ModelDelta(FinishReason: stopped ? "stop" : "length",
                InputTokens: tokens.Length, OutputTokens: outputTokens);
        }
        finally { _gate.Release(); }
    }

    private async Task<LLamaWeights> LoadAsync(CancellationToken cancellationToken)
    {
        if (_weights is not null)
            return _weights;
        var availability = AvailableStatus();
        if (!availability.IsReady)
            throw new InvalidOperationException(availability.Message);

        Volatile.Write(ref _status, new("loading", _options.ModelName, "Loading local GGUF model…", false));
        try
        {
            _weights = await LLamaWeights.LoadFromFileAsync(CreateParameters(), cancellationToken);
            Volatile.Write(ref _status, new("ready", _options.ModelName, "Local model ready", true));
            return _weights;
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _status, AvailableStatus());
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not load the configured GGUF model");
            var message = "Could not load the GGUF model. Check that the file is complete, the native backend matches " +
                          "LLamaSharp, and enough RAM/VRAM is available. " + exception.Message;
            Volatile.Write(ref _status, new("error", _options.ModelName, message, false));
            throw new InvalidOperationException(message, exception);
        }
    }

    private ModelParams CreateParameters()
    {
        LlamaNativeBootstrap.EnsureLoaded();
        return new(_modelPath)
        {
            ContextSize = _options.ContextSize,
            LoadMode = LLamaLoadMode.MemoryMap,
            GpuLayerCount = _options.GpuLayerCount,
            Threads = _options.Threads,
            BatchThreads = _options.Threads,
            BatchSize = 512,
            UBatchSize = 512,
            TypeK = CacheType(_options.CacheTypeK),
            TypeV = CacheType(_options.CacheTypeV),
            FlashAttention = _options.FlashAttention,
        };
    }

    private static GGMLType CacheType(string value) => value switch
    {
        "F16" => GGMLType.GGML_TYPE_F16,
        "Q8_0" => GGMLType.GGML_TYPE_Q8_0,
        "Q4_0" => GGMLType.GGML_TYPE_Q4_0,
        _ => throw new ValidationException("Unsupported KV cache type: " + value),
    };

    private LLamaToken[] TokenizeRequest(LLamaWeights weights, ModelRequest request)
    {
        var adapted = ModelToolProtocol.FormatMessages(request);
        var prompt = FormatPrompt(weights, adapted.SystemPrompt, adapted.Messages);
        // Some embedded templates already include BOS. Avoid adding it twice.
        var bos = weights.Vocab.LLamaTokenToString(weights.Vocab.BOS, isSpecialToken: true);
        var addBos = string.IsNullOrEmpty(bos) || !prompt.StartsWith(bos, StringComparison.Ordinal);
        return weights.Tokenize(prompt, add_bos: addBos, special: true, Encoding.UTF8);
    }

    private string FormatPrompt(LLamaWeights weights, string systemPrompt, IReadOnlyList<InferenceMessage> messages)
    {
        if (_options.ChatTemplate == "DeepSeekCoderV2")
            return DeepSeekPrompt.Format(systemPrompt, messages);

        var template = new LLamaTemplate(weights) { AddAssistant = true };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            template.Add("system", systemPrompt);
        foreach (var message in messages)
            template.Add(message.Role, message.Content);
        return PromptTemplateTransformer.ToModelPrompt(template);
    }

    private ModelStatus AvailableStatus()
    {
        if (_modelPath.Length == 0)
            return new("not_configured", _options.ModelName,
                "Set Llama:ModelPath to a local DeepSeek-Coder-V2-Lite-Instruct GGUF file, then restart the app.", false);
        if (!File.Exists(_modelPath))
            return new("missing", _options.ModelName,
                "The configured GGUF model file was not found. Check Llama:ModelPath.", false);
        return new("available", _options.ModelName, "Local model available; loads on the first message", true);
    }

    private static async Task DecodeAsync(LLamaContext context, LLamaBatch batch, CancellationToken cancellationToken)
    {
        var result = await context.DecodeAsync(batch, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result != DecodeResult.Ok)
            throw new InvalidOperationException($"The local model could not decode this request ({result}).");
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _weights?.Dispose();
            _weights = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
