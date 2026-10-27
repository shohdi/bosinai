namespace Bonsai.Chat.Chat;

public sealed class ChatOptions
{
    public string DataDirectory { get; set; } = "App_Data/sessions";
}

public sealed class ChatApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed record ChatMessage(string Role, string Content);
public sealed record SessionSummary(string Id, string Title, string CreatedAt, string UpdatedAt,
    int MessageCount, string Preview);
public sealed record SessionDetail(string Id, string Title, string CreatedAt, string UpdatedAt,
    int MessageCount, string Preview, IReadOnlyList<ChatMessage> Messages, JobView? ActiveJob);
public sealed record JobView(string Id, string SessionId, string Content, string Status,
    string Progress, string? Error, SessionDetail? Session, string Response);

public sealed class SessionData
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public List<ChatMessage> Messages { get; set; } = [];
}
