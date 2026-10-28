using System.Text;
using Bonsai.Chat.Inference;
using Xunit;

namespace Bonsai.Chat.Tests;

public sealed class InputTailLimiterTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public async Task KeepsLatestInputWithinHalfOfTheSelectedContext(int contextWindow)
    {
        var input = new string('x', 100) + "TAIL";
        var budget = contextWindow / 2;

        var result = await InputTailLimiter.KeepTailAsync(input, budget, CountUtf8Tokens);

        Assert.Equal(input[^budget..], result);
        Assert.Equal(budget, await CountUtf8Tokens(result, CancellationToken.None));
        Assert.EndsWith("TAIL", result);
    }

    [Theory]
    [InlineData("tiny", 8)]
    [InlineData("exactly8", 8)]
    [InlineData("🧠", 4)]
    public async Task InputAtOrBelowTheBudgetIsUnchanged(string input, int budget)
    {
        var result = await InputTailLimiter.KeepTailAsync(input, budget, CountUtf8Tokens);

        Assert.Same(input, result);
    }

    [Theory]
    [InlineData("old🧠", 4, "🧠")]
    [InlineData("old🧠z", 5, "🧠z")]
    [InlineData("old🧠z", 4, "z")]
    [InlineData("oldآب", 4, "آب")]
    public async Task VariableTokenCostsKeepTheSuffixWithoutSplittingUnicode(string input, int budget, string expected)
    {
        var result = await InputTailLimiter.KeepTailAsync(input, budget, CountUtf8Tokens);

        Assert.Equal(expected, result);
        Assert.InRange(await CountUtf8Tokens(result, CancellationToken.None), 0, budget);
        Assert.Equal(result, new UTF8Encoding(false, true).GetString(new UTF8Encoding(false, true).GetBytes(result)));
        Assert.EndsWith(result, input);
    }

    [Fact]
    public async Task TokenCountingCanBeCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var countingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = InputTailLimiter.KeepTailAsync("A long input", 4, async (_, token) =>
        {
            countingStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, cancellation.Token);
        await countingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static Task<int> CountUtf8Tokens(string content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Encoding.UTF8.GetByteCount(content));
    }
}
