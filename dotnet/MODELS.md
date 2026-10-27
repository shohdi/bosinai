# Local model artifacts

DeepSeek-Coder-V2-Lite-Instruct-Q4_K_M.gguf is downloaded from
[bartowski/DeepSeek-Coder-V2-Lite-Instruct-GGUF](https://huggingface.co/bartowski/DeepSeek-Coder-V2-Lite-Instruct-GGUF),
revision `8f248fa`, converted from the
[official DeepSeek model](https://huggingface.co/deepseek-ai/DeepSeek-Coder-V2-Lite-Instruct).

Expected bytes: `10364416768`.

Expected SHA-256 from the upstream Git LFS metadata:
`603bd3f8a0281d16571da7c08bd661ee17ff0d1be6fcbd1b42242da257ef0bb8`.

The download is written to a `.download` file and renamed only after size and
SHA-256 verification. The 16B Lite variant fits this machine's 32 GB system RAM;
the complete 236B model does not. The official
[model license](https://github.com/deepseek-ai/DeepSeek-Coder-V2/blob/main/LICENSE-MODEL)
applies to the model weights.

The complete file was downloaded and its size and SHA-256 verified on
2026-10-27. Both the source project and the self-contained `Published` site have
the installed model under their own `App_Data/models` directories.

Ternary-Bonsai-2-27B-PTQ1_0.gguf and its native runtime were already present in
this repository's ignored model/runtime directories. This custom quantization
uses the matching Prism runtime, rather than the standard LLamaSharp backend.
The C# application owns its native server process and handles its lifecycle.
