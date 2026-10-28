using System.Text.Json;
using Bonsai.Chat.Inference;

namespace Bonsai.Chat.Api;

/// <summary>Optional, bounded process-local response history; CLI requests with store=false never enter it.</summary>
internal sealed class ResponseStateStore
{
    private const int MaxEntries = 64;
    private const long MaxBytes = 32 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private long _bytes;

    internal sealed record Entry(IReadOnlyList<ModelMessage> Messages, JsonElement Response, long Bytes);

    public Entry Get(string id)
    {
        lock (_gate)
            return _entries.TryGetValue(id, out var entry) ? entry
                : throw new OpenAiException(404, "Response was not retained or has expired. Send the full input history, or use store=true.", "response_not_found", "previous_response_id");
    }

    public void Add(string id, IReadOnlyList<ModelMessage> messages, object response)
    {
        var element = JsonSerializer.SerializeToElement(response);
        var size = element.GetRawText().Length * 2L + messages.Sum(message =>
            2L * (message.Content?.Length ?? 0) + (message.ToolCalls?.Sum(call => 2L * call.Arguments.Length) ?? 0));
        if (size > MaxBytes) return;
        lock (_gate)
        {
            while (_order.Count >= MaxEntries || _bytes + size > MaxBytes)
            {
                var oldest = _order.Dequeue();
                if (_entries.Remove(oldest, out var previous)) _bytes -= previous.Bytes;
            }
            _entries.Add(id, new(messages.ToArray(), element, size));
            _order.Enqueue(id);
            _bytes += size;
        }
    }
}
