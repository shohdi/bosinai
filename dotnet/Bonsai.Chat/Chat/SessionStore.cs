using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Bonsai.Chat.Chat;

public sealed partial class SessionStore
{
    private readonly string _directory;
    private readonly ILogger<SessionStore> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public SessionStore(IOptions<ChatOptions> options, IWebHostEnvironment environment,
        ILogger<SessionStore> logger)
    {
        _directory = Path.GetFullPath(options.Value.DataDirectory, environment.ContentRootPath);
        Directory.CreateDirectory(_directory);
        _logger = logger;
    }

    [GeneratedRegex("\\A[A-Za-z0-9_-]{1,80}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private string FilePath(string id)
    {
        if (!IdPattern().IsMatch(id)) throw new ChatApiException(400, "Invalid session ID.");
        return Path.Combine(_directory, id + ".json");
    }

    public IDisposable AcquireLease(string id)
    {
        var path = Path.ChangeExtension(FilePath(id), ".lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ChatApiException(409, "This conversation is open in another application instance."); }
    }

    public SessionData Read(string id)
    {
        var path = FilePath(id);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(file);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                throw new ChatApiException(422, "Unsupported conversation file.");
            var data = root.Deserialize<SessionData>(JsonOptions)!;
            if (data.Messages is null || data.Messages.Any(m => m is null || m.Role is null || m.Content is null))
                throw new ChatApiException(422, "Unsupported conversation file.");
            return data;
        }
        catch (FileNotFoundException) { throw new ChatApiException(404, "Conversation not found."); }
        catch (JsonException) { throw new ChatApiException(422, "This conversation file could not be read."); }
    }

    public SessionData Create(string? title)
    {
        var data = new SessionData { Name = Guid.NewGuid().ToString("N"), Title = title };
        using var lease = AcquireLease(data.Name);
        Save(data.Name, data);
        return data;
    }

    // Callers hold a lease for all writes. Same-directory replacement leaves either the old
    // complete document or the new complete document after an interrupted save.
    public void Save(string id, SessionData data)
    {
        var path = FilePath(id);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        data.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Delete(string id) => File.Delete(FilePath(id));

    public IReadOnlyList<SessionSummary> List()
    {
        var sessions = new List<SessionSummary>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try { sessions.Add(Summary(id, Read(id))); }
            catch (Exception error) when (error is ChatApiException or IOException or UnauthorizedAccessException)
            { _logger.LogWarning("Skipping unreadable conversation {FileName}", Path.GetFileName(path)); }
        }
        return sessions.OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<ChatMessage> VisibleMessages(SessionData data) => data.Messages
        .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrEmpty(m.Content)).ToArray();

    private static string Compact(string value, int limit)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact[..Math.Min(compact.Length, limit)];
    }

    public static SessionSummary Summary(string id, SessionData data)
    {
        var messages = VisibleMessages(data);
        var first = messages.FirstOrDefault(m => m.Role == "user")?.Content ?? "";
        var title = data.Title ?? Compact(first, 60);
        return new SessionSummary(id, string.IsNullOrEmpty(title) ? "New chat" : title,
            data.CreatedAt, data.UpdatedAt, messages.Count,
            messages.Count > 0 ? Compact(messages[^1].Content, 100) : "");
    }

    public static SessionDetail Detail(string id, SessionData data, JobView? activeJob)
    {
        var summary = Summary(id, data);
        return new SessionDetail(id, summary.Title, summary.CreatedAt, summary.UpdatedAt,
            summary.MessageCount, summary.Preview, VisibleMessages(data), activeJob);
    }
}
