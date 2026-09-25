namespace MQESBench.Models;

/// <summary>
/// LLM server metadata automatically detected at benchmark startup.
/// Populated by <c>ServerProbe</c> by querying the server's REST endpoints.
/// Contains low-level architecture, runtime context, and generation parameters
/// extracted directly from the inference server.
/// </summary>
public sealed class ServerMetadata
{
    public string ModelFile { get; set; } = "Unknown";
    public string Quantization { get; set; } = "Unknown";
    public int ContextSize { get; set; } = 4096;
    public string Hardware { get; set; } = "Local Host";

    // Architecture metadata from GGUF /v1/models meta block
    public int TrainingContextSize { get; set; }
    public int VocabularySize { get; set; }
    public int LayerCount { get; set; }
    public int EmbeddingDimension { get; set; }
    public int AttentionHeads { get; set; }
    public int KeyValueHeads { get; set; }
    public double GqaRatio => KeyValueHeads > 0 ? (double)AttentionHeads / KeyValueHeads : 1.0;

    // Runtime slot and generation settings from /props
    public int TotalSlots { get; set; } = 1;
    public int MaxPredictTokens { get; set; }
    public double ServerTemperature { get; set; } = -1;
    public double ServerMinP { get; set; } = -1;
    public double ServerRepeatPenalty { get; set; } = -1;
    public int ServerRepeatLastN { get; set; } = -1;
}