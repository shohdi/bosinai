# Bonsai Chat on Apple Silicon with Metal

These commands build and run **Ternary Bonsai 2 27B PTQ1_0** on an
Apple Silicon Mac. They use a separate `dotnet/Published-Mac` directory and
Mac-specific launch settings. The Windows launcher, configuration, runtime,
models, and conversations are not modified.

The Mac build and Metal inference must be run on the Mac. They have not been
executed or benchmarked on the Windows development machine.

## Install and clone

Use **macOS 14 or later** on an Apple Silicon Mac; the pinned native runtime's
binary deployment target is macOS 14.0. Install a **macOS ARM64 .NET SDK**
capable of targeting .NET 8. The .NET 8 SDK
works; a newer supported SDK can also target this project's `net8.0` framework.
Use the ARM64 SDK and a native Terminal session, rather than an x64 SDK under
Rosetta. The published app includes its .NET runtime, so a separate .NET 8
runtime installation is unnecessary.

You also need Git and internet access for NuGet, the model, and the native
runtime. No Python, Homebrew, or native C++ compilation is required.

Once these scripts have been committed and pushed to GitHub, run in Terminal:

```bash
git clone https://github.com/shohdi/bosinai.git
cd bosinai
uname -m
dotnet --info
bash dotnet/Build-Mac.sh
```

`uname -m` should report `arm64`; `dotnet --info` should identify an ARM64 SDK.
For an existing clone, use `git pull` from its root before building.

The build script runs only on native macOS ARM64. It publishes the application
in Release mode for `osx-arm64`, self-contained, into `dotnet/Published-Mac`.
It also provisions the following files below that directory's `App_Data`:

- The approximately **5.95 GB** `Ternary-Bonsai-2-27B-PTQ1_0.gguf` model,
  pinned to model repository revision `b072e1d3b35a0a630cece372c2127528e0994386`.
- The matching **Prism macOS ARM64 Metal runtime**, pinned to
  [`prism-b10709-9a9394a`](https://github.com/PrismML-Eng/llama.cpp/releases/tag/prism-b10709-9a9394a).

Downloads are verified against pinned SHA-256 hashes; the model's byte count is
also checked. The large model and native binaries are excluded from Git, so
cloning alone does not install them. Keep additional disk space available for
the runtime, .NET publication, and temporary downloads.

## Start and verify

From the repository root:

```bash
bash dotnet/Start-Mac.sh --check
bash dotnet/Start-Mac.sh
```

`--check` verifies the installed model/runtime paths and lists the available
Metal device without starting the website or loading model weights.

Open **http://localhost:5080** and send a short message. The first message loads
the model; starting the website alone does not load it. Keep Terminal open.
Press **Ctrl+C** there to shut down the website and its owned native model
process. Completed conversations remain saved.

The launcher checks that the native runtime exposes a Metal device, selects
Bonsai by default, requests GPU offload and KV-cache offload, and enables native
runtime logging. During the first reply, look for Metal initialization and
messages showing layers offloaded to the GPU. A device listing alone does not
prove that model inference was offloaded.

In a second Terminal window, check:

```bash
curl --fail --silent --show-error http://localhost:5080/api/status
```

Expect `selected_model_id` to be `bonsai`, `context_size` to be `57344`, and the
model state to become `ready` after loading. A successful reply plus native
Metal/offload logs is the practical inference check.

Mac conversations and model selection live under `dotnet/Published-Mac/App_Data`,
independently of the Windows installation. Launch through `Start-Mac.sh` each
time so the Mac runtime paths and Metal settings are applied.

## Context and output length

The default Mac launch preserves the checked-in Bonsai context window:
**57,344 tokens**. It does not reduce that setting. The older
[context verification notes](CONTEXT.md) describe a separately provisioned
Windows configuration with **262,144 tokens**; that is not the current
checked-in `appsettings.json` default.

The existing output policy is unchanged: **there is no fixed response-length
cap**. Output can use the remaining context after the input and the **128-token
safety reserve**. Input, including history and template overhead, is limited to
half the configured context. The model can stop earlier, and an explicit API
`max_tokens`/`max_output_tokens` limit is respected. A finite context window means
output cannot be literally infinite.

If you want the previously documented **256K** Bonsai window, stop the server
and launch with these settings:

```bash
Models__BonsaiContextSize=262144 \
Models__BonsaiCacheTypeK=q4_0 \
Models__BonsaiCacheTypeV=q4_0 \
bash dotnet/Start-Mac.sh
```

This uses quantized keys and values to reduce cache memory. The corresponding
input budget is **131,072 tokens**; output at that input size can use up to
**130,944 tokens**. The command changes only that launch. A normal
`bash dotnet/Start-Mac.sh` restores the published configuration's default window.

The 256K setting was previously exercised on the Windows hardware described in
`CONTEXT.md`. Allocation, speed, and stability at that size remain unverified on
the Mac. Long prompts take more time to process, and other applications compete
for the Mac's unified memory. If memory pressure or loading failures occur,
return to the default launch and inspect the native logs.

## Update or troubleshoot

To update an existing Mac installation, stop the app, pull the repository,
re-run the build script, then start it again:

```bash
git pull
bash dotnet/Build-Mac.sh
bash dotnet/Start-Mac.sh
```

Use the Prism runtime provisioned by the build script for this custom PTQ1_0
model. A generic llama.cpp or another application's bundled engine may not
support that format. Do not copy the Windows `llama-server.exe` onto the Mac.

If the launcher reports no Metal device, confirm that Terminal and the .NET SDK
are running natively as ARM64, inspect the displayed native-runtime error, and
check macOS/runtime compatibility. The launcher fails instead of treating a
CPU-only device listing as confirmation of Metal support.

The website normally uses port **5080**. If another application already uses
that port, stop that application or choose a different URL explicitly when
launching, for example:

```bash
URLS=http://localhost:5081 bash dotnet/Start-Mac.sh
```

Then open and check port 5081 instead.
