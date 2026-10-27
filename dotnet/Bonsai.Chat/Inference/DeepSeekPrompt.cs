using System.Text;

namespace Bonsai.Chat.Inference;

/// <summary>
/// DeepSeek-Coder-V2's official tokenizer template, with BOS supplied by the tokenizer.
/// https://huggingface.co/deepseek-ai/DeepSeek-Coder-V2-Lite-Instruct/blob/main/tokenizer_config.json
/// </summary>
public static class DeepSeekPrompt
{
    public const string EndOfSentence = "<｜end▁of▁sentence｜>";

    public static string Format(string systemPrompt, IReadOnlyList<InferenceMessage> messages)
    {
        var prompt = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            prompt.Append(systemPrompt).Append("\n\n");

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case "user":
                    prompt.Append("User: ").Append(message.Content).Append("\n\n");
                    break;
                case "assistant":
                    prompt.Append("Assistant: ").Append(message.Content).Append(EndOfSentence);
                    break;
                default:
                    throw new ArgumentException("Chat messages must have user or assistant roles.", nameof(messages));
            }
        }

        return prompt.Append("Assistant:").ToString();
    }
}
