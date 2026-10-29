#!/bin/bash
# Isolated Bonsai/Metal launch settings; shared appsettings.json stays untouched.
set -euo pipefail

fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }

[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] ||
    fail 'Run this script on an Apple Silicon Mac in a native ARM64 terminal. No files were changed.'
[[ $# -eq 0 || ( $# -eq 1 && "$1" == --check ) ]] ||
    fail 'Usage: bash dotnet/Start-Mac.sh [--check]'

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
if [[ -f "$script_dir/Bonsai.Chat.dll" ]]; then
    publish_dir=$script_dir
else
    publish_dir="$script_dir/Published-Mac"
fi
[[ -x "$publish_dir/Bonsai.Chat" ]] || fail 'Build first with: bash dotnet/Build-Mac.sh'

export DOTNET_ENVIRONMENT=Production
export ASPNETCORE_ENVIRONMENT=Production
export Models__DefaultModelId=bonsai
export Models__SelectionFile="$publish_dir/App_Data/selected-model.json"
export Chat__DataDirectory="$publish_dir/App_Data/sessions"
export Models__BonsaiModelPath="${Models__BonsaiModelPath:-$publish_dir/App_Data/models/Ternary-Bonsai-2-27B-PTQ1_0.gguf}"
export Models__BonsaiRuntimePath="${Models__BonsaiRuntimePath:-$publish_dir/App_Data/runtime/metal/llama-server}"
export Models__BonsaiGpuLayerCount="${Models__BonsaiGpuLayerCount:-150}"
export Models__BonsaiKvOffload="${Models__BonsaiKvOffload:-true}"
export Models__BonsaiStartupTimeoutSeconds="${Models__BonsaiStartupTimeoutSeconds:-600}"
# Show native device selection, cache placement and offloaded layers on the first reply.
export Logging__LogLevel__Bonsai="${Logging__LogLevel__Bonsai:-Debug}"

[[ -f "$Models__BonsaiModelPath" ]] || fail "Model not found: $Models__BonsaiModelPath"
[[ -x "$Models__BonsaiRuntimePath" ]] || fail "Native runtime not executable: $Models__BonsaiRuntimePath"

# Working from the content root also resolves the published config and wwwroot.
cd -- "$publish_dir"
devices=$("$Models__BonsaiRuntimePath" --list-devices 2>&1) || {
    printf '%s\n' "$devices" >&2
    fail 'The native runtime could not enumerate devices. See dotnet/MACOS.md.'
}
printf '%s\n' "$devices"
grep -Eq '^[[:space:]]*(MTL|Metal)[0-9]+:' <<< "$devices" ||
    fail 'No Metal GPU was reported. Resolve this before running chat; CPU fallback was not started.'

printf '\nMetal is available; Bonsai will request GPU offload.\n'
printf 'Context comes from appsettings.json unless overridden.\n'
if [[ -n "${Models__BonsaiContextSize:-}" ]]; then
    printf 'Context override: %s tokens\n' "$Models__BonsaiContextSize"
fi
printf 'Output has no fixed cap; it can use the remaining context minus the safety reserve.\n'
[[ $# -eq 0 ]] || exit 0
printf 'Open %s and send a message to load the model. Ctrl+C stops the app.\n' "${URLS:-http://localhost:5080}"
exec "$publish_dir/Bonsai.Chat"
