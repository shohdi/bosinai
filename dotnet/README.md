# Bonsai Chat for ASP.NET Core

An independent C# chat application. It does not import, launch, or require Python,
the original chat server, its MCP services, or its configuration.

The existing browser interface is served by ASP.NET Core, including sanitized
Markdown, tables, highlighted code, copy buttons, searchable dated history,
rename/delete, drafts, responsive navigation, live replies, and cancellation.
Conversations are saved as JSON under `Bonsai.Chat/App_Data/sessions`. Each complete
user/assistant turn is saved atomically; stopped or failed turns leave previous
history intact. Separate conversations share a bounded inference queue.

The model dropdown switches between DeepSeek Coder V2 Lite Instruct Q4_K_M and
Ternary Bonsai 2 27B. Selection survives restarts. Switching releases the previous
model, and is blocked while replies are active. DeepSeek uses LLamaSharp directly
inside .NET. Bonsai's custom PTQ1_0 format uses the included repository's Prism
native runtime, started and stopped automatically by C#. Neither backend uses
Python. Models and native runtime files are provisioned under this C# project's
`App_Data/models` and `App_Data/runtime`, outside version control. Local NTFS hard
links share disk storage with the original assets while allowing either directory
to exist independently. The C# app does not need the original source directories.

The provisioned Windows site is in `Published`, with its own .NET/ASP.NET Core
8.0.33 runtime. It runs without the .NET SDK. Its conversations and model selection
are stored in `Published/App_Data`; the source project uses its own `App_Data`.
The app listens on <http://localhost:5080>. `Start-Chat.cmd` launches the site.
The same server exposes `GET /v1/models`, `POST /v1/responses` for Codex CLI,
and `POST /v1/chat/completions` for OpenCode. Browser and API turns share a
single bounded queue and the same loaded model. Use model ID `local` to follow
the web interface's selection. [Coding client examples](clients/README.md)
include configuration and the supported text/tool protocols. API conversations
are separate from saved web chats; no second inference server is required.
The `.sln` includes the web project and integration tests. Configuration is in
`Bonsai.Chat/appsettings.json`; all paths are resolved against the C# project,
not the shell's working directory. No browser assets require a CDN.

Web and API prompts use at most half the selected model's context, including
history, instructions, tools, and template overhead. Older prompt content is
trimmed from the start while keeping the latest input and valid tool exchanges;
saved browser history remains intact. Required instructions/tools that cannot
fit are rejected. Replies may use the remaining context minus a 128-token safety
reserve, without a fixed 1,800-token cap. An explicit API response maximum is
respected. The web chat has no browsing tools. The local API accepts only loopback
hostnames and rejects foreign browser origins.

The configured context windows are 57,344 for DeepSeek and 262,144 for Bonsai,
with input budgets of 28,672 and 131,072 respectively. The client examples
advertise each model's full window. These context allocations were verified
with real cache allocations and replies on this PC. The interface shows the
current limit. [Context verification](CONTEXT.md) records placement and headroom.

Validation: `dotnet test Bonsai.sln`. Tests replace inference with a controlled
model to exercise persistence, concurrent sends, bounded admission, cancellation,
failure recovery, file leases, request validation, model selection and local
browser protections. A fresh child process also verifies the packaged native
CPU backend. Real generation, conversation recall and browser interactions are
smoke tested separately against the provisioned GGUF files.

Pinned dependencies: LLamaSharp and native CPU backend 0.30.0. Browser dependency
versions and licenses are in `wwwroot/static/vendor/THIRD_PARTY.md`.
