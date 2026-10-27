# Context settings verified on this PC

Hardware: 32 GB system RAM, AMD Ryzen 7 7840HS, NVIDIA RTX 4060 Laptop GPU
with 8 GB VRAM. These settings were applied to the source project and the
self-contained Windows site on 2026-10-27.

| Model | Configured context | Cache | Placement |
| --- | ---: | --- | --- |
| DeepSeek Coder V2 Lite Instruct Q4_K_M | 57,344 tokens (56K) | Q4_0 keys and values, Flash Attention | CPU, memory-mapped weights |
| Ternary Bonsai 2 27B PTQ1_0 | 262,144 tokens (256K) | Q4_0 keys and values, Flash Attention | CPU KV cache, 40 GPU layers, batch 128 / micro-batch 64 |

Each setting was tested by allocating the full cache and generating a real reply.
DeepSeek's 56K probe returned `DEEPSEEK_OK`, with minimum available physical RAM
of 2.605 GiB and available commit of 2.285 GiB. A 64K test fell below the chosen
RAM reserve; 96K also caused severe memory pressure, so neither was selected.
The 56K cache uses 4,252.5 MiB and the compute buffer about 236 MiB.

Bonsai's 256K probe returned `BONSAI_CONTEXT_OK`. With the configured placement,
minimum available physical RAM was 10,191 MiB, available commit 1,516 MiB, and
the native runtime reported 2,140 MiB of GPU memory free. Its KV cache uses
4,608 MiB, plus about 150 MiB of recurrent state. This is the model's training
context limit. Higher GPU offload left too little GPU or commit headroom.

Context includes the prompt, conversation, and reserved response tokens. The app
reserves 1,800 output tokens and a 128-token safety margin. It counts the actual
formatted prompt before generation and preserves history when the input is too
large. Old messages are never silently discarded.

The probes validate cache allocation and short replies, rather than retrieval
accuracy over a fully populated context. Large conversations and Bonsai's CPU
cache placement are slower. Available memory varies with other applications;
these are practical tested settings, not a guarantee for every concurrent workload.
