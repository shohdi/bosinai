using System.Text;
using Bonsai.Chat.Inference;

namespace Bonsai.Chat.Chat;

public sealed class ChatCoordinator(SessionStore store, IChatModel model,
    ILogger<ChatCoordinator> logger) : IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Generation> _jobs = new();
    private readonly SemaphoreSlim _modelSlot = new(1, 1);
    private bool _stopping;
    private bool _switching;

    private sealed class Generation(string sessionId, string content)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string SessionId { get; } = sessionId;
        public string Content { get; set; } = content;
        public string Status { get; set; } = "pending";
        public string Progress { get; set; } = "Waiting for the local model…";
        public string? Error { get; set; }
        public SessionDetail? Session { get; set; }
        public StringBuilder Response { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public bool Active => Status is "pending" or "running";
        public JobView View() => new(Id, SessionId, Content, Status, Progress, Error, Session, Response.ToString());
    }

    private Generation? Active(string id) => _jobs.Values.FirstOrDefault(j => j.SessionId == id && j.Active);
    private void RequireIdle(string id)
    {
        if (Active(id) is not null)
            throw new ChatApiException(409, "This conversation is generating a reply. Stop it or wait for it to finish.");
    }

    public IReadOnlyList<SessionSummary> List() { lock (_gate) return store.List(); }
    public IReadOnlyList<JobView> ActiveJobs() { lock (_gate) return _jobs.Values.Where(j => j.Active).Select(j => j.View()).ToArray(); }
    public SessionDetail Detail(string id)
    {
        lock (_gate) return SessionStore.Detail(id, store.Read(id), Active(id)?.View());
    }
    public SessionDetail Create(string? title)
    {
        lock (_gate)
        {
            var data = store.Create(title);
            return SessionStore.Detail(data.Name, data, null);
        }
    }
    public SessionDetail Rename(string id, string title)
    {
        lock (_gate)
        {
            RequireIdle(id);
            using var lease = store.AcquireLease(id);
            var data = store.Read(id);
            data.Title = title;
            store.Save(id, data);
            return SessionStore.Detail(id, data, null);
        }
    }
    public void Delete(string id)
    {
        lock (_gate)
        {
            RequireIdle(id);
            using var lease = store.AcquireLease(id);
            store.Read(id);
            store.Delete(id);
        }
    }

    public JobView Send(string id, string content)
    {
        lock (_gate)
        {
            if (_stopping) throw new ChatApiException(503, "The server is shutting down. Retry after restarting it.");
            if (_switching) throw new ChatApiException(409, "The model is switching. Try sending again in a moment.");
            RequireIdle(id);
            if (_jobs.Values.Count(j => j.Active) >= 8)
                throw new ChatApiException(429, "The model queue is full. Wait for a reply to finish.");
            // Take the file lease before accepting the job, so concurrent app instances cannot
            // produce two replies against the same history snapshot.
            var lease = store.AcquireLease(id);
            SessionData data;
            try { data = store.Read(id); }
            catch { lease.Dispose(); throw; }
            foreach (var old in _jobs.Values.Where(j => !j.Active).SkipLast(99).ToArray())
            {
                _jobs.Remove(old.Id);
                old.Cancellation.Dispose();
            }
            var job = new Generation(id, content);
            _jobs.Add(job.Id, job);
            job.Completion = Task.Run(() => GenerateAsync(job, data, lease));
            return job.View();
        }
    }

    public JobView GetJob(string id)
    {
        lock (_gate) return Find(id).View();
    }

    public async Task SelectModelAsync(ModelCatalog catalog, string id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_switching || _jobs.Values.Any(job => job.Active))
                throw new ChatApiException(409, "Stop active replies or wait for them to finish before switching models.");
            var selected = catalog.ListModels().FirstOrDefault(item => item.Id == id);
            if (selected is null) throw new ChatApiException(400, "Unknown model.");
            if (!selected.Available) throw new ChatApiException(400, selected.Message);
            _switching = true;
        }
        try { await catalog.SelectAsync(id, cancellationToken); }
        finally { lock (_gate) _switching = false; }
    }
    private Generation Find(string id) => _jobs.TryGetValue(id, out var job) ? job
        : throw new ChatApiException(404, "Generation not found. Reload the conversation to see saved messages.");

    public async Task<JobView> CancelAsync(string id)
    {
        Generation job;
        lock (_gate)
        {
            job = Find(id);
            if (job.Active) job.Cancellation.Cancel();
        }
        await job.Completion;
        lock (_gate) return job.View();
    }

    private async Task GenerateAsync(Generation job, SessionData data, IDisposable lease)
    {
        var entered = false;
        var token = job.Cancellation.Token;
        var finalStatus = "failed";
        var finalProgress = "Reply failed.";
        try
        {
            await _modelSlot.WaitAsync(token);
            entered = true;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                job.Status = "running";
                job.Progress = "Loading model / processing conversation…";
            }
            var prepared = await model.PrepareInputAsync(job.Content, token);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                var trimmed = prepared != job.Content;
                job.Content = prepared;
                job.Progress = trimmed
                    ? "Older input trimmed. Processing the end of your message…"
                    : "Processing conversation…";
            }
            var messages = SessionStore.VisibleMessages(data)
                .Select(m => new InferenceMessage(m.Role, m.Content)).ToList();
            messages.Add(new InferenceMessage("user", job.Content));
            await foreach (var chunk in model.GenerateAsync(messages, token).WithCancellation(token))
            {
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    job.Response.Append(chunk);
                    job.Progress = "Writing reply…";
                }
            }
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(job.Response.ToString()))
                    throw new InvalidOperationException("The model returned an empty reply. Retry your message.");
                data.Messages.Add(new ChatMessage("user", job.Content));
                data.Messages.Add(new ChatMessage("assistant", job.Response.ToString()));
                store.Save(job.SessionId, data);
                job.Session = SessionStore.Detail(job.SessionId, data, null);
                finalStatus = "completed";
                finalProgress = "Reply saved.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            finalStatus = "cancelled";
            finalProgress = "Stopped. Your completed conversation is saved.";
        }
        catch (Exception error)
        {
            logger.LogError(error, "Chat generation {JobId} failed", job.Id);
            lock (_gate)
            {
                job.Error = error is InvalidOperationException or ArgumentException or ChatApiException
                    ? error.Message[..Math.Min(error.Message.Length, 600)]
                    : "The reply failed. Check the server logs and model configuration, then retry. Completed messages are saved.";
            }
        }
        finally
        {
            // Release the file before making the finished job observable to mutations.
            lock (_gate)
            {
                lease.Dispose();
                job.Status = finalStatus;
                job.Progress = finalProgress;
            }
            if (entered) _modelSlot.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] tasks;
        lock (_gate)
        {
            _stopping = true;
            foreach (var job in _jobs.Values.Where(j => j.Active)) job.Cancellation.Cancel();
            tasks = _jobs.Values.Select(j => j.Completion).ToArray();
        }
        await Task.WhenAll(tasks).WaitAsync(cancellationToken);
    }
    public void Dispose()
    {
        foreach (var job in _jobs.Values) job.Cancellation.Dispose();
        _modelSlot.Dispose();
    }
}
