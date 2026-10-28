using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bonsai.Chat.Inference;

namespace Bonsai.Chat.Api;

internal sealed class OpenAiException(int status, string message, string code = "invalid_request_error", string? param = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? Param { get; } = param;
}

internal sealed record WireTool(string ModelName, string Name, string? Namespace, bool Custom);
internal sealed record OpenAiRequest(string Model, bool Stream, ModelRequest Completion,
    IReadOnlyDictionary<string, WireTool> Tools, bool Store = true, string? PreviousResponseId = null, bool HasInstructions = false);

/// <summary>Translates the two public wire protocols into the same local inference request.</summary>
internal static class OpenAiRequestParser
{
    private static readonly JsonElement EmptyParameters = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
    private static readonly JsonElement CustomParameters = JsonSerializer.SerializeToElement(new
    {
        type = "object", properties = new { input = new { type = "string", description = "The exact raw text input for this tool." } },
        required = new[] { "input" }, additionalProperties = false
    });

    public static OpenAiRequest Parse(JsonElement body, bool responses)
    {
        var model = RequiredString(body, "model");
        var stream = Boolean(body, "stream", false);
        if (Boolean(body, "background", false)) Unsupported("background", "Background responses are not supported.");
        if (body.TryGetProperty("conversation", out var conversation) && conversation.ValueKind != JsonValueKind.Null)
            Unsupported("conversation", "Use full input history or previous_response_id instead of conversations.");
        if (body.TryGetProperty("n", out var n) && (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var count) || count != 1))
            Unsupported("n", "Only one completion per request is supported.");
        if (body.TryGetProperty("modalities", out var modalities) && (modalities.ValueKind != JsonValueKind.Array
            || modalities.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString() != "text")))
            Unsupported("modalities", "Only text output is supported.");
        if (body.TryGetProperty("response_format", out var format) && format.ValueKind != JsonValueKind.Null
            && OptionalString(format, "type") is not (null or "text"))
            Unsupported("response_format", "Structured response formats are not supported; use function tools.");
        if (body.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.Object
            && text.TryGetProperty("format", out var textFormat) && OptionalString(textFormat, "type") is not (null or "text"))
            Unsupported("text.format", "Structured response formats are not supported; use function tools.");

        var tools = new List<ModelTool>();
        var mappings = new Dictionary<string, WireTool>(StringComparer.Ordinal);
        if (body.TryGetProperty("tools", out var toolValues) && toolValues.ValueKind != JsonValueKind.Null)
        {
            if (toolValues.ValueKind != JsonValueKind.Array) Invalid("tools", "tools must be an array.");
            if (toolValues.GetArrayLength() > 256) Invalid("tools", "At most 256 tools are supported.");
            foreach (var tool in toolValues.EnumerateArray()) AddTool(tool, responses, null, tools, mappings);
        }
        if (body.TryGetProperty("functions", out _)) Unsupported("functions", "Use tools instead of legacy functions.");

        var (choice, forcedName) = ParseToolChoice(body, responses, mappings);
        var messages = responses ? ParseResponsesInput(body, mappings) : ParseChatMessages(body, mappings);
        if (messages.Count == 0 && !body.TryGetProperty("previous_response_id", out _))
            Invalid(responses ? "input" : "messages", "Provide at least one message.");
        var maxTokens = PositiveInteger(body, responses ? "max_output_tokens" : "max_completion_tokens");
        if (!responses && maxTokens is null) maxTokens = PositiveInteger(body, "max_tokens");
        float? temperature = null;
        if (body.TryGetProperty("temperature", out var temp) && temp.ValueKind != JsonValueKind.Null)
        {
            if (temp.ValueKind != JsonValueKind.Number || !temp.TryGetSingle(out var value) || !float.IsFinite(value) || value < 0 || value > 2)
                Invalid("temperature", "temperature must be a number from 0 to 2.");
            temperature = temp.GetSingle();
        }
        var previous = OptionalString(body, "previous_response_id");
        return new(model, stream, new(messages, tools, choice, forcedName, maxTokens, temperature), mappings,
            responses && Boolean(body, "store", true), previous,
            responses && !string.IsNullOrEmpty(OptionalString(body, "instructions")));
    }

    private static List<ModelMessage> ParseChatMessages(JsonElement body, IReadOnlyDictionary<string, WireTool> tools)
    {
        if (!body.TryGetProperty("messages", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new OpenAiException(400, "messages must be an array.", param: "messages");
        var messages = new List<ModelMessage>();
        foreach (var item in values.EnumerateArray())
        {
            var role = RequiredString(item, "role");
            if (role is not ("system" or "developer" or "user" or "assistant" or "tool"))
                Invalid("messages.role", "Unsupported message role.");
            var content = item.TryGetProperty("content", out var value) ? TextContent(value, "messages.content") : null;
            var calls = new List<ModelToolCall>();
            if (item.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind != JsonValueKind.Null)
            {
                if (role != "assistant" || toolCalls.ValueKind != JsonValueKind.Array)
                    Invalid("messages.tool_calls", "Only assistant messages may contain a tool_calls array.");
                foreach (var call in toolCalls.EnumerateArray())
                {
                    if (RequiredString(call, "type") != "function") Unsupported("messages.tool_calls", "Only function tool calls are supported.");
                    if (!call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
                        Invalid("messages.tool_calls.function", "Each tool call requires a function object.");
                    var arguments = RequiredString(function, "arguments", allowEmpty: true);
                    ValidateArguments(arguments, "messages.tool_calls.function.arguments");
                    calls.Add(new(RequiredString(call, "id"), RequiredString(function, "name"), arguments, calls.Count));
                }
            }
            var callId = OptionalString(item, "tool_call_id");
            if (role == "tool" && string.IsNullOrWhiteSpace(callId)) Invalid("messages.tool_call_id", "Tool messages require tool_call_id.");
            if (content is null && calls.Count == 0) Invalid("messages.content", "Message must contain text or tool calls.");
            messages.Add(new(role, content, callId, calls.Count > 0 ? calls : null));
        }
        return messages;
    }

    private static List<ModelMessage> ParseResponsesInput(JsonElement body, IReadOnlyDictionary<string, WireTool> tools)
    {
        var messages = new List<ModelMessage>();
        var instructions = OptionalString(body, "instructions");
        if (!string.IsNullOrEmpty(instructions)) messages.Add(new("system", instructions));
        if (!body.TryGetProperty("input", out var input) || input.ValueKind == JsonValueKind.Null) return messages;
        if (input.ValueKind == JsonValueKind.String) { messages.Add(new("user", input.GetString())); return messages; }
        if (input.ValueKind != JsonValueKind.Array) Invalid("input", "input must be a string or an array of input items.");
        foreach (var item in input.EnumerateArray())
        {
            var type = OptionalString(item, "type") ?? "message";
            switch (type)
            {
                case "message":
                {
                    var role = RequiredString(item, "role");
                    if (role is not ("system" or "developer" or "user" or "assistant")) Invalid("input.role", "Unsupported message role.");
                    if (!item.TryGetProperty("content", out var content)) Invalid("input.content", "Message content is required.");
                    messages.Add(new(role, TextContent(content, "input.content")));
                    break;
                }
                case "function_call":
                case "custom_tool_call":
                {
                    var name = RequiredString(item, "name");
                    var ns = OptionalString(item, "namespace");
                    var modelName = ResolveName(name, ns, tools);
                    var args = type == "custom_tool_call"
                        ? JsonSerializer.Serialize(new { input = RequiredString(item, "input", allowEmpty: true) })
                        : RequiredString(item, "arguments", allowEmpty: true);
                    ValidateArguments(args, "input.arguments");
                    var call = new ModelToolCall(RequiredString(item, "call_id"), modelName, args);
                    // The native chat templates expect a parallel call batch in one assistant turn.
                    if (messages.LastOrDefault() is { Role: "assistant", ToolCalls: not null } previous)
                        messages[^1] = previous with { ToolCalls = previous.ToolCalls.Concat(new[] { call }).ToArray() };
                    else messages.Add(new("assistant", null, ToolCalls: new[] { call }));
                    break;
                }
                case "function_call_output":
                case "custom_tool_call_output":
                    if (!item.TryGetProperty("output", out var output)) Invalid("input.output", "Tool output is required.");
                    messages.Add(new("tool", TextContent(output, "input.output"), RequiredString(item, "call_id")));
                    break;
                case "reasoning":
                    // No local model produces encrypted reasoning. Empty summaries can safely roundtrip.
                    if (item.TryGetProperty("encrypted_content", out var encrypted) && encrypted.ValueKind != JsonValueKind.Null
                        && (encrypted.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(encrypted.GetString())))
                        Unsupported("input.reasoning", "Encrypted reasoning from another provider cannot be decoded locally.");
                    if (item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
                    {
                        var summaryText = new StringBuilder();
                        foreach (var part in summary.EnumerateArray())
                            summaryText.Append(RequiredString(part, "text", allowEmpty: true));
                        if (summaryText.Length > 0) messages.Add(new("assistant", summaryText.ToString()));
                    }
                    break;
                default: Unsupported("input.type", $"Input item type '{type}' is not supported by this text-only local API."); break;
            }
        }
        return messages;
    }

    private static void AddTool(JsonElement value, bool responses, string? ns, List<ModelTool> tools, Dictionary<string, WireTool> mappings)
    {
        var type = RequiredString(value, "type");
        if (responses && type == "namespace")
        {
            if (ns is not null) Invalid("tools", "Nested namespaces are not supported.");
            var name = RequiredString(value, "name");
            if (!value.TryGetProperty("tools", out var children) || children.ValueKind != JsonValueKind.Array)
                Invalid("tools", "A namespace requires a tools array.");
            foreach (var child in children.EnumerateArray()) AddTool(child, true, name, tools, mappings);
            return;
        }
        var custom = responses && type == "custom";
        if (type != "function" && !custom) Unsupported("tools.type", $"Tool type '{type}' is not supported. Define client-executed function or custom tools.");
        var definition = value;
        if (!responses && (!value.TryGetProperty("function", out definition) || definition.ValueKind != JsonValueKind.Object))
            Invalid("tools.function", "Function tools require a function object.");
        var originalName = RequiredString(definition, "name");
        if (originalName.Length > 64 || originalName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            Invalid("tools.name", "Tool names must contain 1-64 ASCII letters, digits, underscores or hyphens.");
        var modelName = ModelName(originalName, ns);
        if (!mappings.TryAdd(modelName, new(modelName, originalName, ns, custom))) Invalid("tools", "Tool names must be unique within their namespace.");
        var description = OptionalString(definition, "description") ?? "";
        var parameters = custom ? CustomParameters : definition.TryGetProperty("parameters", out var schema) ? schema.Clone() : EmptyParameters;
        if (parameters.ValueKind != JsonValueKind.Object) Invalid("tools.parameters", "Tool parameters must be a JSON schema object.");
        if (custom && definition.TryGetProperty("format", out var customFormat) && customFormat.ValueKind == JsonValueKind.Object
            && OptionalString(customFormat, "type") == "grammar")
            description += "\nThe input string must satisfy the tool grammar:\n" + customFormat.GetRawText();
        tools.Add(new(modelName, description, parameters));
        if (tools.Count > 256) Invalid("tools", "At most 256 tools are supported.");
    }

    private static (string Choice, string? Name) ParseToolChoice(JsonElement body, bool responses, IReadOnlyDictionary<string, WireTool> tools)
    {
        if (!body.TryGetProperty("tool_choice", out var value) || value.ValueKind == JsonValueKind.Null) return ("auto", null);
        if (value.ValueKind == JsonValueKind.String)
        {
            var choice = value.GetString()!;
            if (choice is not ("auto" or "none" or "required")) Invalid("tool_choice", "Use auto, none, required, or a specific function.");
            if (choice == "required" && tools.Count == 0) Invalid("tool_choice", "tool_choice required needs at least one tool.");
            return (choice, null);
        }
        if (value.ValueKind != JsonValueKind.Object) Invalid("tool_choice", "Invalid tool_choice.");
        var type = RequiredString(value, "type");
        if (type is not ("function" or "custom")) Unsupported("tool_choice", "Only function and custom tool choices are supported.");
        var definition = !responses && value.TryGetProperty("function", out var function) ? function : value;
        var name = ResolveName(RequiredString(definition, "name"), OptionalString(definition, "namespace"), tools);
        if (!tools.ContainsKey(name)) Invalid("tool_choice", "The selected tool is not defined in tools.");
        return ("required", name);
    }

    private static string ResolveName(string name, string? ns, IReadOnlyDictionary<string, WireTool> tools)
    {
        var found = tools.Values.FirstOrDefault(tool => tool.Name == name && tool.Namespace == ns);
        return found?.ModelName ?? ModelName(name, ns);
    }

    private static string ModelName(string name, string? ns)
    {
        if (ns is null) return name;
        var raw = ns + "__" + name;
        if (raw.Length <= 64 && raw.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return raw;
        return "tool_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..48];
    }

    private static void ValidateArguments(string arguments, string param)
    {
        try
        {
            using var parsed = JsonDocument.Parse(arguments);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) Invalid(param, "Tool arguments must be a JSON object encoded as a string.");
        }
        catch (JsonException) { Invalid(param, "Tool arguments must be a JSON object encoded as a string."); }
    }

    private static string? TextContent(JsonElement value, string param)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind != JsonValueKind.Array) Invalid(param, "Text content must be a string or text-part array.");
        var result = new StringBuilder();
        foreach (var part in value.EnumerateArray())
        {
            var type = RequiredString(part, "type");
            if (type is not ("text" or "input_text" or "output_text")) Unsupported(param, $"Content type '{type}' is not supported by this text-only local API.");
            result.Append(RequiredString(part, "text", allowEmpty: true));
        }
        return result.ToString();
    }

    internal static string RequiredString(JsonElement value, string name, bool allowEmpty = false)
    {
        var result = OptionalString(value, name);
        if (result is null || (!allowEmpty && string.IsNullOrWhiteSpace(result))) Invalid(name, $"{name} must be a nonempty string.");
        return result!;
    }

    private static string? OptionalString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) Invalid(name, "Expected an object.");
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) Invalid(name, $"{name} must be a string.");
        return property.GetString();
    }

    private static bool Boolean(JsonElement value, string name, bool fallback)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return fallback;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Invalid(name, $"{name} must be a boolean.");
        return property.GetBoolean();
    }

    private static int? PositiveInteger(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var result) || result < 1)
            Invalid(name, $"{name} must be a positive integer.");
        return property.GetInt32();
    }

    private static void Invalid(string param, string message) => throw new OpenAiException(400, message, param: param);
    private static void Unsupported(string param, string message) => throw new OpenAiException(400, message, "unsupported_feature", param);
}
