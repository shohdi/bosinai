namespace Bonsai.Chat.Inference;

public sealed class ModelCatalogOptions
{
    public string DefaultModelId { get; set; } = "deepseek";
    public string RepositoryRoot { get; set; } = "";
    public string SelectionFile { get; set; } = "App_Data/selected-model.json";
    public string BonsaiModelPath { get; set; } = "";
    public string BonsaiRuntimePath { get; set; } = "";
    public uint BonsaiContextSize { get; set; } = 8192;
    public int BonsaiGpuLayerCount { get; set; } = 99;
    public int BonsaiStartupTimeoutSeconds { get; set; } = 180;
    public string BonsaiCacheTypeK { get; set; } = "f16";
    public string BonsaiCacheTypeV { get; set; } = "f16";
    public bool BonsaiKvOffload { get; set; } = true;
    public int BonsaiBatchSize { get; set; } = 128;
    public int BonsaiUBatchSize { get; set; } = 64;
}
