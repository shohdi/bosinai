# Local coding clients

The running ASP.NET Core application serves the chat UI and the API at the same address. With its default port, the API base URL is `http://localhost:5080/v1`. Use the application's actual port if it differs. No second model process or Python service is needed.

`local` always uses the model selected in the web UI. Requests from the UI and coding clients share one queue and the same loaded model. Finish or cancel active requests before switching models. The client examples use explicit `bonsai` and `deepseek` model IDs so each model advertises its full context. Select the matching model in the web interface first; an unselected model ID returns an error instead of loading another model. Client conversations do not enter the web chat history.

## Codex CLI

[codex.config.toml](codex.config.toml) defines a custom provider using streaming Responses, with Bonsai's full 262,144-token context as the default. Merge its settings into Codex's user-level `config.toml`. The example disables WebSocket transport and hosted web search, and uses local conversation compaction. No OpenAI login or API key is required by this loopback API.

The optional [bosinai-bonsai.config.toml](bosinai-bonsai.config.toml) and [bosinai-deepseek.config.toml](bosinai-deepseek.config.toml) files belong beside that user-level `config.toml`. Select them with `codex --profile bosinai-bonsai` or `codex --profile bosinai-deepseek`. The DeepSeek profile advertises its actual 57,344-token window. These profiles inherit the shared provider connection; neither sets a response-token cap.

The tested Codex release uses 95% of the configured window for its own effective-context accounting: it reports 54,476 for the 57,344-token DeepSeek profile. The server still applies its half-window input policy and allocates output from the model's actual remaining context.

The provider fields and separate profile files follow the official [Codex custom provider and profile documentation](https://learn.chatgpt.com/docs/config-file/config-advanced) and [configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference). Current Codex versions require `wire_api = "responses"`; profile files use top-level keys, not legacy `[profiles.*]` tables.

## OpenCode

[opencode.json](opencode.json) defines `bosinai/bonsai` and `bosinai/deepseek` through `@ai-sdk/openai-compatible`, which uses Chat Completions. Keep [opencode-bosinai.js](opencode-bosinai.js) beside the JSON file when copying this example into the client project. If merging into an existing configuration, include its `provider`, `plugin`, and `compaction` settings. Select the matching model in OpenCode's model picker after selecting it in the web interface. No API key is required by the server.

The small local plugin clears OpenCode's default `maxOutputTokens` only for provider `bosinai`, letting the server allocate the actual remaining context for each reply. Automatic client compaction is disabled in this example so the server's oldest-first input trimming controls the retained conversation. No environment-variable override or extra plugin dependency is needed. The per-model `limit.output` values describe the theoretical context ceiling after the 128-token safety reserve; actual replies also subtract the formatted input tokens.

The configuration follows the official [OpenCode custom provider](https://opencode.ai/docs/providers/#custom-provider) and [plugin documentation](https://opencode.ai/docs/plugins/). The output hook is supported by OpenCode 1.19.4's [plugin interface](https://github.com/anomalyco/opencode/blob/v1.19.4/packages/plugin/src/index.ts); it removes that version's [default output-token restriction](https://github.com/anomalyco/opencode/blob/v1.19.4/packages/opencode/src/session/llm/request.ts).

## Context and tools

The web interface, Responses API, and Chat Completions API apply the same token policy:

| Selected model | Full context | Maximum formatted input | Output budget when input fills its half |
| --- | ---: | ---: | ---: |
| Bonsai 2 27B | 262,144 | 131,072 | 130,944 |
| DeepSeek Coder V2 Lite | 57,344 | 28,672 | 28,544 |

Input includes retained history, instructions, tool definitions, and chat-template overhead. When needed, older input is removed from the start while retaining the latest input and valid tool exchanges. Required instructions/tool definitions that cannot fit are rejected. Saved web chat history remains intact; trimming affects the prompt sent for inference.

There is no fixed 1,800-token output cap. Without a caller-specified maximum, the output budget is `context window - actual formatted input tokens - 128`. Shorter input therefore leaves more than half the context available for output. If an API caller supplies `max_output_tokens`, `max_completion_tokens`, or `max_tokens`, that request's smaller maximum is still respected. Generation can finish earlier when the model emits its end token or a tool call. `GET /v1/models` reports the selected model's current context.

The API streams text and tool calls. Codex/OpenCode execute their tools and send the results back; the web server does not execute those tools. DeepSeek uses a structured tool prompt adapter, while Bonsai uses its native chat template. Correct tool selection and code quality depend on the selected model. Images, audio, and hosted provider tools are outside this local text API.

These examples are inert files. They do not modify your global Codex/OpenCode configuration or start another server.

## Validation

The compatibility tests use a deterministic fake model to cover HTTP/SSE protocols, tool arguments and results, cancellation, shared inference, and history isolation without loading another GGUF. Run the .NET suite with `dotnet test dotnet/Bonsai.sln`. Real client checks also completed against this fake backend: Codex CLI `0.164.0-alpha.11.1` completed a Responses function-call/result round trip, and OpenCode `1.19.4` completed a Chat Completions call that read a local test fixture. The Codex shell operation was denied by the test environment's command policy; its error result still completed the protocol round trip. These checks verify client compatibility; they do not measure a GGUF model's tool-selection quality.

The updated Codex profile and OpenCode configuration/plugin were exercised with those clients. All five captured API requests omitted output-token caps. The full .NET suite passed 106 tests, including prompt trimming and remaining-context output budgeting.
