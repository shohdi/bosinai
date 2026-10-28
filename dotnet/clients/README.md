# Local coding clients

The running ASP.NET Core application serves the chat UI and the API at the same address. With its default port, the API base URL is `http://localhost:5080/v1`. Use the application's actual port if it differs. No second model process or Python service is needed.

`local` always uses the model selected in the web UI. Requests from the UI and coding clients share one queue and the same loaded model. Finish or cancel active requests before switching models. An explicit model ID that is not selected returns an error instead of loading another model. Client conversations do not enter the web chat history.

## Codex CLI

[codex.config.toml](codex.config.toml) defines a custom provider using streaming Responses. Copy its settings into a separate Codex profile/config when you want to use the local model. The example disables WebSocket transport and hosted web search, and uses local conversation compaction. No OpenAI login or API key is required by this loopback API.

The provider fields follow the official [Codex custom provider documentation](https://learn.chatgpt.com/docs/config-file/config-advanced#custom-model-providers) and [configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference). Current Codex versions require `wire_api = "responses"`.

## OpenCode

[opencode.json](opencode.json) defines `bosinai/local` through `@ai-sdk/openai-compatible`, which uses Chat Completions. Place this example in the client project's configuration or merge its `provider` entry with an existing OpenCode configuration. Select `bosinai/local` in the model picker. No API key is required by the server.

The configuration follows the official [OpenCode custom provider documentation](https://opencode.ai/docs/providers/#custom-provider).

## Context and tools

The examples advertise 57,344 tokens, which fits either currently configured model. This is a client budget and does not change server tuning. When using Bonsai exclusively, set the client context to the `context_window` reported for the selected model by `GET /v1/models` (currently 262,144). Keep the client output budget within the server's configured generation limit (currently 1,800). If the prompt, tool definitions, and reserved output exceed the real model context, the API returns an error so the client can compact its history.

The API streams text and tool calls. Codex/OpenCode execute their tools and send the results back; the web server does not execute those tools. DeepSeek uses a structured tool prompt adapter, while Bonsai uses its native chat template. Correct tool selection and code quality depend on the selected model. Images, audio, and hosted provider tools are outside this local text API.

These examples are inert files. They do not modify your global Codex/OpenCode configuration or start another server.

## Validation

The compatibility tests use a deterministic fake model, so they cover HTTP/SSE protocols, tool arguments and results, cancellation, shared inference, and history isolation without loading another GGUF. The .NET suite passed 93 tests. Real client checks also completed against this fake backend: Codex CLI `0.164.0-alpha.11.1` completed a Responses function-call/result round trip, and OpenCode `1.19.4` completed a Chat Completions call that read a local test fixture. The Codex shell operation was denied by the test environment's command policy; its error result still completed the protocol round trip. These checks verify client compatibility; they do not measure a GGUF model's tool-selection quality.
