using System.Runtime.CompilerServices;
using Bonsai.Chat.Chat;

namespace Bonsai.Chat.Inference;

/// <summary>One bounded queue for browser turns and coding-client requests.</summary>
public sealed class SharedInferenceService(IChatModel model, ModelCatalog catalog,
    IHostApplicationLifetime lifetime)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private int _admitted;
    private bool _switching;

    public int ActiveCount { get { lock (_gate) return _admitted; } }

    public Reservation Reserve()
    {
        lock (_gate)
        {
            if (lifetime.ApplicationStopping.IsCancellationRequested)
                throw new ChatApiException(503, "The server is shutting down.");
            if (_switching)
                throw new ChatApiException(409, "The model is switching. Retry in a moment.");
            if (_admitted >= 8)
                throw new ChatApiException(429, "The shared model queue is full. Wait for a reply to finish.");
            _admitted++;
            return new Reservation(this);
        }
    }

    public void ValidateModel(string modelId)
    {
        if (modelId is "local" or "selected" || modelId == catalog.SelectedId) return;
        if (catalog.ListModels().Any(item => item.Id == modelId))
            throw new ChatApiException(409, "This model is not selected. Choose it in the web interface, or use model 'local' to share the current selection.");
        throw new ChatApiException(404, "Unknown model. Use model 'local' to share the model selected in the web interface.");
    }

    public async IAsyncEnumerable<ModelDelta> GenerateAsync(string modelId, ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateModel(modelId);
        using var reservation = Reserve();
        await reservation.EnterAsync(cancellationToken);
        // Recheck after waiting: every request must use the model it actually requested.
        ValidateModel(modelId);
        await foreach (var delta in model.CompleteAsync(request, reservation.Token)
            .WithCancellation(reservation.Token))
            yield return delta;
    }

    public async Task SelectModelAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_switching || _admitted != 0)
                throw new ChatApiException(409, "Wait for browser and API replies to finish before switching models.");
            _switching = true;
        }
        try { await catalog.SelectAsync(id, cancellationToken); }
        finally { lock (_gate) _switching = false; }
    }

    public sealed class Reservation : IDisposable
    {
        private readonly SharedInferenceService _owner;
        private CancellationTokenSource? _cancellation;
        private bool _entered;
        private int _disposed;

        internal Reservation(SharedInferenceService owner) => _owner = owner;
        public CancellationToken Token => _cancellation?.Token ?? _owner.lifetimeToken;

        public async Task EnterAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_cancellation is not null) throw new InvalidOperationException("The inference reservation is already in use.");
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _owner.lifetimeToken);
            await _owner._slot.WaitAsync(_cancellation.Token);
            _entered = true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_entered) _owner._slot.Release();
            lock (_owner._gate) _owner._admitted--;
            _cancellation?.Dispose();
        }
    }

    private CancellationToken lifetimeToken => lifetime.ApplicationStopping;
}
