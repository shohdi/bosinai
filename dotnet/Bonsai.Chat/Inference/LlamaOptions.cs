using System.ComponentModel.DataAnnotations;

namespace Bonsai.Chat.Inference;

public sealed class LlamaOptions
{
    public string ModelPath { get; set; } = "";
    public string ModelName { get; set; } = "DeepSeek-Coder-V2-Lite-Instruct";

    // DeepSeek's official template is explicit because some GGUF conversions omit it.
    // Use Model to apply another GGUF's embedded chat template instead.
    public string ChatTemplate { get; set; } = "DeepSeekCoderV2";

    [Range(512, 131072)]
    public uint ContextSize { get; set; } = 8192;

    [Range(0, int.MaxValue)]
    public int GpuLayerCount { get; set; }

    [Range(0, 32768)]
    public int SafetyMarginTokens { get; set; } = 128;

    [Range(0, 2)]
    public float Temperature { get; set; } = 0.2f;

    [Range(0.01, 1)]
    public float TopP { get; set; } = 0.95f;

    [Range(1, 1024)]
    public int? Threads { get; set; }

    [RegularExpression("^(F16|Q8_0|Q4_0)$")]
    public string CacheTypeK { get; set; } = "F16";

    [RegularExpression("^(F16|Q8_0|Q4_0)$")]
    public string CacheTypeV { get; set; } = "F16";

    public bool? FlashAttention { get; set; }

    public string SystemPrompt { get; set; } =
        "You are a helpful coding assistant. Answer accurately and use Markdown when it helps.";
}
