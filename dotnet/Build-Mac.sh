#!/bin/bash
# Build on the Apple Silicon Mac itself. Never modifies the Windows publish folder.
set -euo pipefail

fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }

[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] ||
    fail 'Run this script on an Apple Silicon Mac in a native ARM64 terminal. No files were changed.'
[[ $# -eq 0 ]] || fail 'Usage: bash dotnet/Build-Mac.sh'

for tool in dotnet curl shasum tar; do
    command -v "$tool" >/dev/null 2>&1 || fail "Required command not found: $tool"
done
[[ -n "$(dotnet --list-sdks)" ]] || fail 'Install an ARM64 .NET SDK (8 or later), then retry.'
sdk_info=$(DOTNET_CLI_UI_LANGUAGE=en dotnet --info)
grep -Eq 'RID:[[:space:]]+osx-arm64' <<< "$sdk_info" ||
    fail 'Install/use the ARM64 .NET SDK, rather than an x64 SDK running under Rosetta.'

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
publish_dir="$script_dir/Published-Mac"
data_dir="$publish_dir/App_Data"
runtime_dir="$data_dir/runtime/metal"
model_name=Ternary-Bonsai-2-27B-PTQ1_0.gguf
model_revision=b072e1d3b35a0a630cece372c2127528e0994386
model_sha256=53107f530aa52eb00912263ab1ee29bd199261c87cd7b4ad4ca1318c1fe33ee3
model_size=5946648928
runtime_tag=prism-b10709-9a9394a
runtime_archive="llama-$runtime_tag-bin-macos-arm64.tar.gz"
runtime_sha256=f9cdf245fb7b832f1996dd776b321d4ae1f23b6d88c380100f636742c3a980ff

verify_file() {
    local file=$1 expected_hash=$2 expected_size=${3:-} actual_hash
    [[ -f "$file" ]] || return 1
    if [[ -n "$expected_size" ]]; then
        [[ $(wc -c < "$file") -eq $expected_size ]] || return 1
    fi
    actual_hash=$(shasum -a 256 "$file")
    [[ "${actual_hash%% *}" == "$expected_hash" ]]
}

download_verified() {
    local url=$1 target=$2 digest=$3 size=${4:-} partial
    partial="$target.download"
    if [[ -f "$target" ]]; then
        verify_file "$target" "$digest" "$size" ||
            fail "Existing file failed verification; move it aside before retrying: $target"
        printf 'Verified existing %s\n' "${target##*/}"
        return
    fi
    mkdir -p -- "$(dirname -- "$target")"
    printf 'Downloading %s (interrupted downloads can be resumed)\n' "${target##*/}"
    curl --fail --location --retry 3 --continue-at - --output "$partial" "$url"
    verify_file "$partial" "$digest" "$size" ||
        fail "Download failed SHA-256/size verification. Remove $partial and retry."
    mv -- "$partial" "$target"
}

printf 'Publishing the application for macOS ARM64 into %s\n' "$publish_dir"
dotnet publish "$script_dir/Bonsai.Chat/Bonsai.Chat.csproj" \
    --configuration Release --runtime osx-arm64 --self-contained true \
    --output "$publish_dir"

download_verified \
    "https://github.com/PrismML-Eng/llama.cpp/releases/download/$runtime_tag/$runtime_archive" \
    "$data_dir/downloads/$runtime_archive" "$runtime_sha256"
mkdir -p -- "$runtime_dir"
# The verified archive has one top-level llama-<tag> directory, including its dylibs.
tar -xzf "$data_dir/downloads/$runtime_archive" --strip-components=1 -C "$runtime_dir"
[[ -f "$runtime_dir/llama-server" && -f "$runtime_dir/libggml-metal.dylib" ]] ||
    fail 'The verified runtime did not contain llama-server and the Metal backend.'
chmod u+x "$runtime_dir/llama-server"

download_verified \
    "https://huggingface.co/prism-ml/Ternary-Bonsai-2-27B-gguf/resolve/$model_revision/$model_name" \
    "$data_dir/models/$model_name" "$model_sha256" "$model_size"

# Also make the published directory usable independently of the checkout.
cp -- "$script_dir/Start-Mac.sh" "$publish_dir/Start-Mac.sh"
printf '\nBuild complete. Start Bonsai with:\n  bash "%s/Start-Mac.sh"\n' "$script_dir"
printf 'Windows files and settings were not changed. Mac chats survive subsequent builds.\n'
