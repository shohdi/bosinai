using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bonsai.Chat.Inference;

public sealed record AvailableModel(string Id, string Name, string Backend, bool Available, string Message, uint ContextSize = 0);

/// <summary>Owns the selected local model and releases its resources before switching.</summary>
public sealed class ModelCatalog : IChatModel, IAsyncDisposable
{
    private readonly LlamaOptions _deepSeekOptions;
    private readonly ModelCatalogOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggers;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private readonly string _bonsaiModelPath;
    private readonly string _bonsaiRuntimePath;
    private readonly string _selectionFile;
    private string _selectedId;
    private IChatModel _active;
    private ModelStatus? _transitionStatus;
    private bool _disposed;

    public ModelCatalog(IOptions<LlamaOptions> deepSeekOptions, IOptions<ModelCatalogOptions> options,
        IHostEnvironment environment, ILoggerFactory loggers)
    {
        _options = options.Value;
        _environment = environment;
        _loggers = loggers;
        var repositoryRoot = FindRepositoryRoot(environment.ContentRootPath, _options.RepositoryRoot);
        string AssetPath(string configured, string relative)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured, environment.ContentRootPath);
            var bundled = Path.Combine(environment.ContentRootPath, "App_Data", relative);
            return File.Exists(bundled) ? bundled : Path.Combine(repositoryRoot, relative);
        }
        var original = deepSeekOptions.Value;
        _deepSeekOptions = new LlamaOptions
        {
            ModelPath = AssetPath(original.ModelPath, "models/DeepSeek-Coder-V2-Lite-Instruct-Q4_K_M.gguf"),
            ModelName = original.ModelName,
            ChatTemplate = original.ChatTemplate,
            ContextSize = original.ContextSize,
            GpuLayerCount = original.GpuLayerCount,
            MaxTokens = original.MaxTokens,
            SafetyMarginTokens = original.SafetyMarginTokens,
            Temperature = original.Temperature,
            TopP = original.TopP,
            Threads = original.Threads,
            CacheTypeK = original.CacheTypeK,
            CacheTypeV = original.CacheTypeV,
            FlashAttention = original.FlashAttention,
            SystemPrompt = original.SystemPrompt,
        };
        _bonsaiModelPath = AssetPath(_options.BonsaiModelPath, "models/Ternary-Bonsai-2-27B-PTQ1_0.gguf");
        _bonsaiRuntimePath = AssetPath(_options.BonsaiRuntimePath, "runtime/cuda/llama-server.exe");
        _selectionFile = Path.GetFullPath(_options.SelectionFile, environment.ContentRootPath);
        _selectedId = _options.DefaultModelId is "deepseek" or "bonsai" ? _options.DefaultModelId : "deepseek";
        try
        {
            if (File.Exists(_selectionFile))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_selectionFile));
                var saved = document.RootElement.GetProperty("selected_id").GetString();
                if (saved is "deepseek" or "bonsai") _selectedId = saved;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            loggers.CreateLogger<ModelCatalog>().LogWarning(error, "Could not read saved model selection");
        }
        _active = CreateModel(_selectedId);
    }

    public string SelectedId => Volatile.Read(ref _selectedId);
    public uint SelectedContextSize => SelectedId == "bonsai" ? _options.BonsaiContextSize : _deepSeekOptions.ContextSize;

    public IReadOnlyList<AvailableModel> ListModels() =>
    [
        new("deepseek", _deepSeekOptions.ModelName, "LLamaSharp · CPU",
            File.Exists(_deepSeekOptions.ModelPath), File.Exists(_deepSeekOptions.ModelPath)
                ? "Local coding model · Q4_K_M" : "DeepSeek GGUF file is missing", _deepSeekOptions.ContextSize),
        new("bonsai", "Ternary Bonsai 2 · 27B", "Prism llama.cpp · CUDA",
            File.Exists(_bonsaiModelPath) && File.Exists(_bonsaiRuntimePath),
            !File.Exists(_bonsaiModelPath) ? "Bonsai GGUF file is missing" : !File.Exists(_bonsaiRuntimePath)
                ? "Bonsai native runtime is missing" : "Local general assistant · PTQ1_0", _options.BonsaiContextSize),
    ];

    public ModelStatus GetStatus()
    {
        // A read that began before a switch finishes before disposal starts. Reads during
        // disposal see this status instead of touching native resources being released.
        lock (_statusGate) return _transitionStatus ?? _active.GetStatus();
    }

    public async Task SelectAsync(string id, CancellationToken cancellationToken = default)
    {
        var available = ListModels().FirstOrDefault(model => model.Id == id)
            ?? throw new ArgumentException("Unknown model. Choose DeepSeek or Bonsai.", nameof(id));
        if (!available.Available)
            throw new InvalidOperationException(available.Message);
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("A reply is being generated. Stop it or wait before switching models.");
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (id == _selectedId)
                return;
            cancellationToken.ThrowIfCancellationRequested();
            var next = CreateModel(id);
            var releasing = false;
            try
            {
                // Persist before releasing the current model so a disk error leaves it usable.
                await SaveSelectionAsync(id);
                lock (_statusGate)
                {
                    _transitionStatus = new("switching", available.Name, "Switching local model…", false);
                    releasing = true;
                }
                if (_active is IAsyncDisposable previous)
                    await previous.DisposeAsync();
            }
            catch
            {
                if (releasing)
                    lock (_statusGate)
                        _transitionStatus = new("error", available.Name,
                            "Could not release the previous model. Retry the selection.", false);
                if (next is IAsyncDisposable disposable) await disposable.DisposeAsync();
                throw;
            }
            lock (_statusGate)
            {
                _active = next;
                Volatile.Write(ref _selectedId, id);
                _transitionStatus = null;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<string> PrepareInputAsync(string content, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await _active.PrepareInputAsync(content, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<string> GenerateAsync(IReadOnlyList<InferenceMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await foreach (var chunk in _active.GenerateAsync(messages, cancellationToken))
                yield return chunk;
        }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<ModelDelta> CompleteAsync(ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await foreach (var delta in _active.CompleteAsync(request, cancellationToken))
                yield return delta;
        }
        finally { _gate.Release(); }
    }

    private IChatModel CreateModel(string id) => id == "deepseek"
        ? new LlamaChatModel(Options.Create(_deepSeekOptions), _environment, _loggers.CreateLogger<LlamaChatModel>())
        : new BonsaiChatModel(_bonsaiModelPath, _bonsaiRuntimePath,
            Path.GetDirectoryName(_selectionFile)!, _options, _deepSeekOptions.SystemPrompt,
            _deepSeekOptions.SafetyMarginTokens, _loggers.CreateLogger<BonsaiChatModel>());

    private async Task SaveSelectionAsync(string id)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_selectionFile)!);
        var temporary = _selectionFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { selected_id = id }));
            File.Move(temporary, _selectionFile, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string FindRepositoryRoot(string contentRoot, string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured, contentRoot);
        for (var directory = new DirectoryInfo(contentRoot); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "models"))) return directory.FullName;
        return contentRoot;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            lock (_statusGate)
            {
                _disposed = true;
                _transitionStatus = new("stopped", _active.GetStatus().ModelName, "The local model is stopping.", false);
            }
            if (_active is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
        finally { _gate.Release(); }
    }
}
