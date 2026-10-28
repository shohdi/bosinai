using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Bonsai.Chat.Inference;

public sealed record ModelTool(string Name, string Description, JsonElement Parameters);
public sealed record ModelToolCall(string Id, string Name, string Arguments, int Index = 0);
public sealed record ModelMessage(string Role, string? Content, string? ToolCallId = null,
    IReadOnlyList<ModelToolCall>? ToolCalls = null);
public sealed record ModelRequest(IReadOnlyList<ModelMessage> Messages, IReadOnlyList<ModelTool>? Tools = null,
    string ToolChoice = "auto", string? ForcedToolName = null, int? MaxTokens = null, float? Temperature = null);
public sealed record ModelDelta(string? Text = null, int ToolIndex = 0, string? ToolCallId = null,
    string? ToolName = null, string? Arguments = null, string? FinishReason = null,
    int? InputTokens = null, int? OutputTokens = null);

internal static class ModelCompletionFallback
{
    public static async IAsyncEnumerable<ModelDelta> CompleteAsync(IChatModel model, ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ModelToolProtocol.ValidateRequest(request);
        var adapted = ModelToolProtocol.FormatMessages(request);
        var messages = adapted.Messages.ToList();
        if (adapted.SystemPrompt.Length > 0)
            messages.Insert(0, new InferenceMessage("user", "Instructions:\n" + adapted.SystemPrompt));
        if (messages[^1].Role != "user")
            messages.Add(new InferenceMessage("user", "Continue the supplied conversation."));
        var text = new System.Text.StringBuilder();
        var useTools = request.Tools is { Count: > 0 } && request.ToolChoice != "none";
        await foreach (var chunk in model.GenerateAsync(messages, cancellationToken))
        {
            if (useTools) text.Append(chunk);
            else yield return new ModelDelta(Text: chunk);
        }
        if (useTools)
            foreach (var delta in ModelToolProtocol.ParseResponse(text.ToString(), request))
                yield return delta;
        else yield return new ModelDelta(FinishReason: "stop");
    }
}
