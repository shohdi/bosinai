using System.Text.Json;
using Bonsai.Chat.Inference;
using Xunit;

namespace Bonsai.Chat.Tests;

public sealed class ModelToolProtocolTests
{
    [Fact]
    public void CallerInstructionsAndCorrelatedToolHistorySurviveTextTemplateAdaptation()
    {
        var request = Request() with { Messages = new ModelMessage[]
        {
            new("system", "Use the caller policy"), new("developer", "Keep replies concise"),
            new("user", "Read notes"), new("assistant", null, ToolCalls: [new("call_read", "read_file", "{\"path\":\"notes.txt\"}")]),
            new("tool", "Saved notes", "call_read")
        } };
        var adapted = ModelToolProtocol.FormatMessages(request, "Default policy should be replaced");
        Assert.Contains("Use the caller policy", adapted.SystemPrompt);
        Assert.Contains("Keep replies concise", adapted.SystemPrompt);
        Assert.DoesNotContain("Default policy should be replaced", adapted.SystemPrompt);
        Assert.Contains("read_file", adapted.SystemPrompt);
        Assert.Equal("assistant", adapted.Messages[1].Role);
        Assert.Contains("call_read", adapted.Messages[1].Content);
        Assert.Contains("notes.txt", adapted.Messages[1].Content);
        Assert.Equal("user", adapted.Messages[2].Role);
        Assert.Contains("call_read", adapted.Messages[2].Content);
        Assert.Contains("Saved notes", adapted.Messages[2].Content);
    }

    [Fact]
    public void ValidToolEnvelopePreservesArgumentsAndAssignsDistinctCorrelatedCalls()
    {
        const string reply = "{\"content\":\"Reading files\",\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"first.txt\"}},{\"name\":\"read_file\",\"arguments\":{\"path\":\"second.txt\"}}]}";
        var deltas = ModelToolProtocol.ParseResponse(reply, Request());
        Assert.Equal("Reading files", deltas[0].Text);
        var calls = deltas.Where(delta => delta.ToolName is not null).ToArray();
        Assert.Equal(new[] { 0, 1 }, calls.Select(call => call.ToolIndex));
        Assert.Equal(2, calls.Select(call => call.ToolCallId).Distinct().Count());
        Assert.All(calls, call => Assert.StartsWith("call_", call.ToolCallId));
        using var arguments = JsonDocument.Parse(calls[1].Arguments!);
        Assert.Equal("second.txt", arguments.RootElement.GetProperty("path").GetString());
        Assert.Equal("tool_calls", deltas.Last().FinishReason);
    }

    [Theory]
    [InlineData("{\"tool_calls\":[{\"name\":\"undefined\",\"arguments\":{\"path\":\"notes.txt\"}}]}")]
    [InlineData("{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{}}]}")]
    [InlineData("{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":13}}]}")]
    [InlineData("{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"notes.txt\",\"surprise\":true}}]}")]
    [InlineData("{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":[]}]}")]
    [InlineData("{\"tool_calls\":\"wrong\"}")]
    public void MalformedOrUndefinedCallsFailBeforeClientsCanExecuteThem(string reply)
        => Assert.Throws<InvalidOperationException>(() => ModelToolProtocol.ParseResponse(reply, Request()));

    [Fact]
    public void ToolChoiceAutoKeepsNormalAnswersAndNoneKeepsJsonAsText()
    {
        const string text = "A normal explanation with Markdown **code**.";
        Assert.Equal(text, ModelToolProtocol.ParseResponse(text, Request())[0].Text);
        const string json = "{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"notes.txt\"}}]}";
        var none = Request() with { ToolChoice = "none" };
        Assert.Equal(json, ModelToolProtocol.ParseResponse(json, none)[0].Text);
        Assert.DoesNotContain("Tool definitions:", ModelToolProtocol.FormatMessages(none).SystemPrompt);
    }

    [Theory]
    [InlineData("Plain answer")]
    [InlineData("{}")]
    [InlineData("{\"tool_calls\":[]}")]
    public void RequiredToolCannotSilentlyBecomePlainText(string reply)
        => Assert.Throws<InvalidOperationException>(() => ModelToolProtocol.ParseResponse(reply, Request() with { ToolChoice = "required" }));

    [Fact]
    public void ForcedToolRejectsDifferentDefinedTool()
    {
        var request = Request();
        var tool = request.Tools!.Single();
        request = request with { Tools = [tool, new("other", "Other tool", tool.Parameters)], ForcedToolName = "read_file" };
        Assert.Throws<InvalidOperationException>(() => ModelToolProtocol.ParseResponse("{\"tool_calls\":[{\"name\":\"other\",\"arguments\":{\"path\":\"notes.txt\"}}]}", request));
    }

    [Fact]
    public void NativeTemplateCoalescesDeveloperInstructionsWithoutChangingOrdinaryHistory()
    {
        ModelMessage[] history = [new("user", "Read notes"), new("assistant", null, ToolCalls: [new("call_read", "read_file", "{\"path\":\"notes.txt\"}")]), new("tool", "Saved notes", "call_read")];
        var input = new[] { new ModelMessage("system", "System policy"), history[0], new ModelMessage("developer", "Developer policy"), history[1], history[2] };
        var normalized = ModelToolProtocol.NormalizeSystemMessages(input);
        Assert.Equal("system", normalized[0].Role);
        Assert.Equal("System policy\n\nDeveloper policy", normalized[0].Content);
        Assert.Equal(history, normalized.Skip(1));
        Assert.All(normalized.Skip(1).Zip(history), pair => Assert.Same(pair.Second, pair.First));
    }

    [Fact]
    public void UnsupportedRolesAndUncorrelatedToolResultsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ModelToolProtocol.ValidateRequest(Request() with { Messages = [new("image", "unsupported")] }));
        Assert.Throws<ArgumentException>(() => ModelToolProtocol.ValidateRequest(Request() with { Messages = [new("tool", "missing id")] }));
        Assert.Throws<ArgumentException>(() => ModelToolProtocol.ValidateRequest(Request() with { ForcedToolName = "undefined" }));
        Assert.Throws<ArgumentException>(() => ModelToolProtocol.ValidateRequest(Request() with { Tools = [], ToolChoice = "required" }));
    }

    private static ModelRequest Request()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}");
        return new ModelRequest([new("user", "Read notes")], [new("read_file", "Read a text file", schema.RootElement.Clone())]);
    }
}
