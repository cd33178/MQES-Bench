namespace MQESBench.Models;

/// <summary>
/// LLM server metadata automatically detected at benchmark startup.
/// Populated by <c>ServerProbe</c> by querying the server's REST endpoints.
/// </summary>
public sealed record ServerMetadata
{
    /// <summary>GGUF filename or model identifier currently loaded on the server.</summary>
    public string ModelFile { get; set; } = "llama-server";

    /// <summary>Detected quantization scheme (e.g., Q4_K_M, Q8_0, F16).</summary>
    public string Quantization { get; set; } = "Unknown";

    /// <summary>Host hardware description (hostname, core count, OS).</summary>
    public string Hardware { get; set; } = "Local Host";

    /// <summary>Active context window size in tokens (n_ctx).</summary>
    public int ContextSize { get; set; } = 16384;
}