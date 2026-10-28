namespace Bonsai.Chat.Inference;

public sealed record InferenceMessage(string Role, string Content);

public sealed record ModelStatus(string State, string ModelName, string Message, bool IsReady);

public interface IChatModel
{
    ModelStatus GetStatus();

    Task<string> PrepareInputAsync(string content, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> GenerateAsync(
        IReadOnlyList<InferenceMessage> messages,
        CancellationToken cancellationToken = default);
}
