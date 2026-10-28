using System.Text.Json;

namespace Bonsai.Chat.Inference;

/// <summary>
/// Adapts structured agent turns for GGUF models whose native template has no tool protocol.
/// Tools are descriptions only: callers execute returned calls and send back their results.
/// </summary>
public static class ModelToolProtocol
{
    /// <summary>Native Jinja templates commonly accept system rather than the API's developer role.</summary>
    public static IReadOnlyList<ModelMessage> NormalizeSystemMessages(IReadOnlyList<ModelMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var instructions = messages.Where(message => message.Role is "system" or "developer").ToArray();
        if (instructions.Length == 0) return messages;
        var result = new List<ModelMessage>
        {
            new("system", string.Join("\n\n", instructions.Select(message => message.Content ?? ""))),
        };
        result.AddRange(messages.Where(message => message.Role is not ("system" or "developer")));
        return result;
    }

    public static void ValidateRequest(ModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages is null || request.Messages.Count == 0)
            throw new ArgumentException("At least one input message is required.");
        if (request.Messages.Any(message => message.Role is not ("system" or "developer" or "user" or "assistant" or "tool")))
            throw new ArgumentException("Unsupported input message role.");
        if (request.Messages.Any(message => message.Role == "tool" && string.IsNullOrWhiteSpace(message.ToolCallId)))
            throw new ArgumentException("Tool results require a tool call id.");
        if (request.ToolChoice is not ("auto" or "none" or "required"))
            throw new ArgumentException("Tool choice must be auto, none, or required.");
        if (request.MaxTokens is <= 0 || request.Temperature is { } temperature &&
            (!float.IsFinite(temperature) || temperature < 0 || temperature > 2))
            throw new ArgumentException("Output tokens must be positive and temperature must be between 0 and 2.");
        var tools = request.Tools ?? [];
        if (tools.Any(tool => string.IsNullOrWhiteSpace(tool.Name) || tool.Parameters.ValueKind != JsonValueKind.Object) ||
            tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new ArgumentException("Tools require unique names and object parameter schemas.");
        if (request.ToolChoice == "required" && tools.Count == 0)
            throw new ArgumentException("A required tool choice needs at least one tool.");
        if (request.ForcedToolName is { } forced &&
            (request.ToolChoice == "none" || !tools.Any(tool => tool.Name == forced)))
            throw new ArgumentException("The forced tool must be one of the supplied tools.");
    }

    public static (string SystemPrompt, IReadOnlyList<InferenceMessage> Messages) FormatMessages(
        ModelRequest request, string defaultSystemPrompt = "")
    {
        ValidateRequest(request);
        var instructions = request.Messages.Where(message => message.Role is "system" or "developer")
            .Select(message => message.Content ?? "").ToList();
        if (instructions.Count == 0 && !string.IsNullOrWhiteSpace(defaultSystemPrompt))
            instructions.Add(defaultSystemPrompt);
        if (request.Tools is { Count: > 0 } && request.ToolChoice != "none")
        {
            var definitions = JsonSerializer.Serialize(request.Tools.Select(tool => new
            {
                name = tool.Name, description = tool.Description, parameters = tool.Parameters,
            }));
            var choice = request.ForcedToolName is { } name
                ? $"You must call the tool named {JsonSerializer.Serialize(name)}."
                : request.ToolChoice == "required" ? "You must call at least one supplied tool."
                : "Call a tool when needed; otherwise answer normally in plain text.";
            instructions.Add("You can request the following tools from the client. The client executes them; " +
                "do not claim to have executed a tool yourself. " + choice + "\n" +
                "For tool requests output ONLY a JSON object with this structure: " +
                "{\"content\":null,\"tool_calls\":[{\"name\":\"tool_name\",\"arguments\":{}}]}. " +
                "Use the tool's exact name and provide a JSON object matching its parameter schema. " +
                "Do not put tool request JSON inside Markdown fences. Tool definitions:\n" + definitions);
        }
        var messages = new List<InferenceMessage>();
        foreach (var message in request.Messages)
        {
            if (message.Role is "system" or "developer") continue;
            if (message.Role == "tool")
            {
                messages.Add(new("user", "Tool result for call " + message.ToolCallId + ":\n" + (message.Content ?? "")));
                continue;
            }
            var content = message.Content ?? "";
            if (message.ToolCalls is { Count: > 0 })
                content += (content.Length == 0 ? "" : "\n") + JsonSerializer.Serialize(new
                {
                    tool_calls = message.ToolCalls.Select(call => new
                    {
                        id = call.Id, name = call.Name, arguments = ParseArgumentObject(call.Arguments),
                    }),
                });
            messages.Add(new(message.Role, content));
        }
        // A system-only request still needs the final assistant generation cue in the template.
        if (messages.Count == 0) messages.Add(new("user", "Follow the supplied instructions."));
        return (string.Join("\n\n", instructions), messages);
    }

    public static IReadOnlyList<ModelDelta> ParseResponse(string response, ModelRequest request)
    {
        ValidateRequest(request);
        if (request.ToolChoice == "none" || request.Tools is not { Count: > 0 })
            return [new(Text: response), new(FinishReason: "stop")];
        var required = request.ToolChoice == "required" || request.ForcedToolName is not null;
        var json = response.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal) && json.EndsWith("```", StringComparison.Ordinal))
        {
            var newline = json.IndexOf('\n');
            if (newline >= 0) json = json[(newline + 1)..^3].Trim();
        }
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException error)
        {
            if (required) throw new InvalidOperationException("The local model did not return valid JSON for the required tool call.", error);
            return [new(Text: response), new(FinishReason: "stop")];
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tool_calls", out var calls))
            {
                if (required) throw new InvalidOperationException("The local model did not return the required tool call.");
                return [new(Text: response), new(FinishReason: "stop")];
            }
            if (calls.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("The local model returned an invalid tool_calls value.");
            var result = new List<ModelDelta>();
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String &&
                content.GetString() is { Length: > 0 } text)
                result.Add(new(Text: text));
            var index = 0;
            foreach (var call in calls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("name", out var nameValue) ||
                    nameValue.ValueKind != JsonValueKind.String || !call.TryGetProperty("arguments", out var arguments))
                    throw new InvalidOperationException("The local model returned an incomplete tool call.");
                var name = nameValue.GetString();
                var tool = request.Tools.FirstOrDefault(tool => tool.Name == name)
                    ?? throw new InvalidOperationException("The local model requested an undefined tool.");
                if (request.ForcedToolName is { } forced && name != forced)
                    throw new InvalidOperationException("The local model did not call the requested tool.");
                JsonElement value;
                try { value = arguments.ValueKind == JsonValueKind.String ? ParseArgumentObject(arguments.GetString()!) : arguments; }
                catch (ArgumentException error) { throw new InvalidOperationException("The local model returned invalid tool arguments.", error); }
                if (value.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("The local model returned tool arguments that are not a JSON object.");
                ValidateValue(value, tool.Parameters, tool.Name);
                result.Add(new(ToolIndex: index++, ToolCallId: "call_" + Guid.NewGuid().ToString("N"),
                    ToolName: tool.Name, Arguments: value.GetRawText()));
            }
            if (index == 0 && required)
                throw new InvalidOperationException("The local model did not return the required tool call.");
            result.Add(new(FinishReason: index > 0 ? "tool_calls" : "stop"));
            return result;
        }
    }

    private static JsonElement ParseArgumentObject(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Tool arguments must be a JSON object.");
            return document.RootElement.Clone();
        }
        catch (JsonException error) { throw new ArgumentException("Tool arguments must be valid JSON.", error); }
    }

    private static void ValidateValue(JsonElement value, JsonElement schema, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("type", out var type))
        {
            bool Matches(string? expected) => expected switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number == decimal.Truncate(number),
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => true,
            };
            var valid = type.ValueKind == JsonValueKind.String ? Matches(type.GetString()) :
                type.ValueKind != JsonValueKind.Array || type.EnumerateArray().Any(item => Matches(item.GetString()));
            if (!valid) throw new InvalidOperationException($"The local model returned an invalid argument type at {path}.");
        }
        if (schema.TryGetProperty("enum", out var enumeration) && enumeration.ValueKind == JsonValueKind.Array &&
            !enumeration.EnumerateArray().Any(item => item.GetRawText() == value.GetRawText()))
            throw new InvalidOperationException($"The local model returned an invalid argument value at {path}.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                foreach (var key in required.EnumerateArray())
                    if (key.ValueKind == JsonValueKind.String && !value.TryGetProperty(key.GetString()!, out _))
                        throw new InvalidOperationException($"The local model omitted required argument {path}.{key.GetString()}.");
            var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
            var extraAllowed = !schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False;
            foreach (var property in value.EnumerateObject())
                if (hasProperties && properties.TryGetProperty(property.Name, out var nested))
                    ValidateValue(property.Value, nested, path + "." + property.Name);
                else if (!extraAllowed)
                    throw new InvalidOperationException($"The local model returned unexpected argument {path}.{property.Name}.");
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray()) ValidateValue(item, items, path + "[]");
    }
}
