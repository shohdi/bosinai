namespace Bonsai.Chat.Inference;

/// <summary>One prompt budget and remaining-context output policy for browser and API requests.</summary>
public static class ModelContextPolicy
{
    public static async Task<ModelRequest> PrepareAsync(ModelRequest request, int contextSize,
        Func<ModelRequest, CancellationToken, Task<int>> countTokens,
        CancellationToken cancellationToken = default)
    {
        ModelToolProtocol.ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(countTokens);
        ArgumentOutOfRangeException.ThrowIfLessThan(contextSize, 2);
        var budget = contextSize / 2;
        cancellationToken.ThrowIfCancellationRequested();
        if (await CountAsync(request) <= budget) return request;

        // Instructions and schemas stay intact. Conversation text is the truncatable input.
        var instructions = request.Messages.Where(message => message.Role is "system" or "developer").ToArray();
        var conversation = request.Messages.Where(message => message.Role is not ("system" or "developer")).ToList();
        ModelRequest Current() => request with { Messages = instructions.Concat(conversation).ToArray() };

        // Discard complete oldest turns before shortening the latest turn.
        while (true)
        {
            var firstUser = conversation.FindIndex(message => message.Role == "user");
            var nextUser = firstUser < 0 ? -1 : conversation.FindIndex(firstUser + 1, message => message.Role == "user");
            if (nextUser < 0) break;
            conversation.RemoveRange(0, nextUser);
            if (await CountAsync(Current()) <= budget) return Current();
        }

        // Keep each assistant call with its result messages. Older tool rounds can be
        // removed as a unit; the newest round and latest user request remain correlated.
        var groups = new List<List<ModelMessage>>();
        for (var index = 0; index < conversation.Count; index++)
        {
            var group = new List<ModelMessage> { conversation[index] };
            if (conversation[index].ToolCalls is { Count: > 0 })
                while (index + 1 < conversation.Count && conversation[index + 1].Role == "tool")
                    group.Add(conversation[++index]);
            groups.Add(group);
        }
        for (var index = 0; index < groups.Count - 1;)
        {
            if (groups[index].Any(message => message.Role == "user")) { index++; continue; }
            groups.RemoveAt(index);
            conversation = groups.SelectMany(group => group).ToList();
            if (await CountAsync(Current()) <= budget) return Current();
        }

        // Trim text from the start, counting the entire rendered prompt on every probe.
        // Call IDs, names, arguments, definitions and caller instructions are never sliced.
        for (var index = 0; index < conversation.Count; index++)
        {
            var message = conversation[index];
            if (string.IsNullOrEmpty(message.Content)) continue;
            conversation[index] = message with { Content = "" };
            if (await CountAsync(Current()) > budget) continue;
            var slot = index;
            var tail = await InputTailLimiter.KeepTailAsync(message.Content, budget, async (text, token) =>
            {
                conversation[slot] = message with { Content = text };
                return await countTokens(Current(), token);
            }, cancellationToken);
            conversation[index] = message with { Content = tail };
            var prepared = Current();
            if (await CountAsync(prepared) <= budget) return prepared;
        }
        if (await CountAsync(Current()) <= budget) return Current();
        throw new InvalidOperationException($"Context full: instructions, tool definitions, or required tool-call data exceed the half-window input limit of {budget:N0} tokens. Reduce those fields.");

        async Task<int> CountAsync(ModelRequest candidate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await countTokens(candidate, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return count;
        }
    }

    public static int RemainingOutputTokens(int contextSize, int inputTokens, int safetyMargin = 128,
        int? requestedMaxTokens = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(safetyMargin);
        if (requestedMaxTokens is <= 0) throw new ArgumentOutOfRangeException(nameof(requestedMaxTokens));
        var remaining = (long)contextSize - inputTokens - safetyMargin;
        if (remaining < 1) throw new InvalidOperationException("Context full: no space remains for a reply.");
        return (int)Math.Min(remaining, requestedMaxTokens ?? int.MaxValue);
    }
}
