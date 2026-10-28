using System.Text;
using System.Text.Json;
using Bonsai.Chat.Inference;
using Xunit;

namespace Bonsai.Chat.Tests;

public sealed class ModelContextPolicyTests
{
    [Fact]
    public async Task RequestThatFitsKeepsInstructionsMessagesAndToolDefinitions()
    {
        var request = Request([new("system", "Caller policy"), new("developer", "Developer policy"), new("user", "Read notes")]);

        var prepared = await ModelContextPolicy.PrepareAsync(request, 2048, CountTokens);

        Assert.Equal(request.Messages, prepared.Messages);
        Assert.Same(request.Tools, prepared.Tools);
        Assert.Equal(request.ToolChoice, prepared.ToolChoice);
        Assert.Equal(request.MaxTokens, prepared.MaxTokens);
    }

    [Fact]
    public async Task FullPromptBudgetIncludesInstructionsTemplatesAndToolDefinitions()
    {
        var instructions = new ModelMessage("system", "Keep the caller policy");
        var request = Request([instructions, new("user", new string('x', 800) + "NEWEST_SUFFIX")]);

        var prepared = await ModelContextPolicy.PrepareAsync(request, 512, CountTokens);

        Assert.InRange(await CountTokens(prepared, CancellationToken.None), 1, 256);
        Assert.Contains(instructions, prepared.Messages);
        Assert.Same(request.Tools, prepared.Tools);
        Assert.Equal(request.Tools![0].Parameters.GetRawText(), prepared.Tools![0].Parameters.GetRawText());
        var text = prepared.Messages.Last().Content!;
        Assert.EndsWith("NEWEST_SUFFIX", text);
        Assert.EndsWith(text, request.Messages.Last().Content);
        Assert.True(text.Length < request.Messages.Last().Content!.Length);
    }

    [Fact]
    public async Task OldestUserAssistantTurnIsDiscardedBeforeTheNewestTurn()
    {
        var request = new ModelRequest([
            new("system", "policy"),
            new("user", new string('a', 200)),
            new("assistant", "OLD_ANSWER"),
            new("user", "LATEST_QUESTION")
        ]);

        var prepared = await ModelContextPolicy.PrepareAsync(request, 120, CountTokens);

        Assert.InRange(await CountTokens(prepared, CancellationToken.None), 1, 60);
        Assert.DoesNotContain(prepared.Messages, message => message.Content == "OLD_ANSWER");
        Assert.DoesNotContain(prepared.Messages, message => message.Content == new string('a', 200));
        Assert.Equal("LATEST_QUESTION", prepared.Messages.Last().Content);
        Assert.Equal("policy", prepared.Messages[0].Content);
    }

    [Fact]
    public async Task OldToolCallAndItsResultAreRemovedTogether()
    {
        var request = Request([
            new("system", "Caller policy"),
            new("user", "Old question"),
            new("assistant", null, ToolCalls: [new("call_old", "read_file", "{\"path\":\"old.txt\"}")]),
            new("tool", new string('x', 3000), "call_old"),
            new("assistant", "Old completed answer"),
            new("user", "LATEST_QUESTION")
        ]);

        var prepared = await ModelContextPolicy.PrepareAsync(request, 1024, CountTokens);

        Assert.InRange(await CountTokens(prepared, CancellationToken.None), 1, 512);
        Assert.DoesNotContain(prepared.Messages, message => message.ToolCallId == "call_old");
        Assert.DoesNotContain(prepared.Messages, message => message.ToolCalls?.Any(call => call.Id == "call_old") == true);
        Assert.DoesNotContain(prepared.Messages, message => message.Content == "Old completed answer");
        Assert.Equal("LATEST_QUESTION", prepared.Messages.Last().Content);
        ModelToolProtocol.ValidateRequest(prepared);
    }

    [Fact]
    public async Task LatestLargeToolResultKeepsItsTailAndCorrelatedCall()
    {
        var call = new ModelToolCall("call_latest", "read_file", "{\"path\":\"latest.txt\"}");
        var result = new string('x', 3000) + "LATEST_TOOL_RESULT";
        var request = Request([
            new("system", "Caller policy"),
            new("user", "Read latest file"),
            new("assistant", null, ToolCalls: [call]),
            new("tool", result, "call_latest")
        ]);

        var prepared = await ModelContextPolicy.PrepareAsync(request, 1024, CountTokens);

        Assert.InRange(await CountTokens(prepared, CancellationToken.None), 1, 512);
        var retainedCall = Assert.Single(prepared.Messages.SelectMany(message => message.ToolCalls ?? []));
        Assert.Equal(call, retainedCall);
        var retainedResult = Assert.Single(prepared.Messages, message => message.Role == "tool");
        Assert.Equal(call.Id, retainedResult.ToolCallId);
        Assert.EndsWith("LATEST_TOOL_RESULT", retainedResult.Content);
        Assert.EndsWith(retainedResult.Content!, result);
        ModelToolProtocol.ValidateRequest(prepared);
    }

    [Fact]
    public async Task ParallelToolResultsStayPairedWithTheirOriginalArguments()
    {
        ModelToolCall[] calls = [new("call_first", "read_file", "{\"path\":\"first.txt\"}", 0),
            new("call_second", "read_file", "{\"path\":\"second.txt\"}", 1)];
        var request = Request([
            new("user", "Read both files"),
            new("assistant", null, ToolCalls: calls),
            new("tool", new string('x', 3000), "call_first"),
            new("tool", new string('y', 3000) + "LATEST_SECOND_RESULT", "call_second")
        ]);

        var prepared = await ModelContextPolicy.PrepareAsync(request, 2048, CountTokens);

        Assert.InRange(await CountTokens(prepared, CancellationToken.None), 1, 1024);
        Assert.Equal(calls, prepared.Messages.SelectMany(message => message.ToolCalls ?? []).ToArray());
        Assert.Equal(new[] { "call_first", "call_second" }, prepared.Messages.Where(message => message.Role == "tool")
            .Select(message => message.ToolCallId).ToArray());
        Assert.EndsWith("LATEST_SECOND_RESULT", prepared.Messages.Last().Content);
        ModelToolProtocol.ValidateRequest(prepared);
    }

    [Fact]
    public async Task OversizedImmutableInstructionsAreRejectedInsteadOfSilentlyChanged()
    {
        var request = new ModelRequest([new("system", new string('s', 500)), new("user", "Latest")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModelContextPolicy.PrepareAsync(request, 512, CountTokens));

        Assert.Equal(new string('s', 500), request.Messages[0].Content);
    }

    [Fact]
    public async Task OversizedToolDefinitionsAreRejectedInsteadOfCorruptingTheSchema()
    {
        var request = Request([new("user", "Latest")]);
        request = request with { Tools = [request.Tools![0] with { Description = new string('d', 1000) }] };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModelContextPolicy.PrepareAsync(request, 512, CountTokens));

        Assert.Equal(new string('d', 1000), request.Tools![0].Description);
        Assert.Equal(JsonValueKind.Object, request.Tools[0].Parameters.ValueKind);
    }

    [Fact]
    public async Task RequestTokenizationCanBeCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = ModelContextPolicy.PrepareAsync(new([new("user", "text")]), 512, async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData(262144, 131072, 130944)]
    [InlineData(57344, 28672, 28544)]
    [InlineData(262144, 100, 261916)]
    public void OutputCanUseTheEntireRemainingContextWithoutTheOldFixedCap(int contextSize, int inputTokens, int expected)
    {
        var remaining = ModelContextPolicy.RemainingOutputTokens(contextSize, inputTokens);

        Assert.Equal(expected, remaining);
        Assert.True(remaining > 1800);
    }

    [Fact]
    public void ExplicitClientOutputLimitIsHonoredWithinTheRemainingContext()
    {
        Assert.Equal(5000, ModelContextPolicy.RemainingOutputTokens(262144, 131072, requestedMaxTokens: 5000));
        Assert.Equal(130944, ModelContextPolicy.RemainingOutputTokens(262144, 131072, requestedMaxTokens: 200000));
    }

    private static ModelRequest Request(IReadOnlyList<ModelMessage> messages)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" }
        });
        return new(messages, [new("read_file", "Read a text file", schema)]);
    }

    private static Task<int> CountTokens(ModelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = 5;
        foreach (var message in request.Messages)
        {
            count += 3 + Encoding.UTF8.GetByteCount(message.Content ?? "");
            if (message.ToolCalls is not null)
                foreach (var call in message.ToolCalls)
                    count += Encoding.UTF8.GetByteCount(call.Id + call.Name + call.Arguments);
            count += Encoding.UTF8.GetByteCount(message.ToolCallId ?? "");
        }
        if (request.Tools is not null)
            foreach (var tool in request.Tools)
                count += Encoding.UTF8.GetByteCount(tool.Name + tool.Description + tool.Parameters.GetRawText());
        return Task.FromResult(count);
    }
}
