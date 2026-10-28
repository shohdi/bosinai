namespace Bonsai.Chat.Inference;

/// <summary>Keeps an exact suffix of input text within a model's token budget.</summary>
public static class InputTailLimiter
{
    public static async Task<string> KeepTailAsync(
        string content,
        int tokenBudget,
        Func<string, CancellationToken, Task<int>> countTokensAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(countTokensAsync);
        ArgumentOutOfRangeException.ThrowIfLessThan(tokenBudget, 1);
        cancellationToken.ThrowIfCancellationRequested();

        if (content.Length == 0)
            return content;
        var tokenCount = await countTokensAsync(content, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (tokenCount <= tokenBudget)
            return content;

        var tooLongStart = 0;
        var fittingStart = content.Length;
        while (fittingStart - tooLongStart > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = tooLongStart + (fittingStart - tooLongStart) / 2;
            // A UTF-16 surrogate pair is one Unicode scalar: never cut it in half.
            if (char.IsLowSurrogate(content[start]) && char.IsHighSurrogate(content[start - 1]))
                start++;
            if (start == fittingStart)
                break;

            // Tokenize the suffix itself; token boundaries can change after trimming.
            if (await countTokensAsync(content[start..], cancellationToken) <= tokenBudget)
                fittingStart = start;
            else
                tooLongStart = start;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return content[fittingStart..];
    }
}
