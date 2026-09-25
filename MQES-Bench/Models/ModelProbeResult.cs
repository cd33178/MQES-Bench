namespace MQESBench.Models;

/// <summary>
/// Empirical runtime metrics and tool-calling capabilities gathered via active probing.
/// </summary>
public record ModelProbeResult
{
    public bool ExecutedSuccessfully { get; init; }
    public bool HasNativeToolCalls { get; init; }
    public string DetectedToolSyntax { get; init; } = "None";
    public string RawResponseContent { get; init; } = string.Empty;
    public double TokensPerSecond { get; init; }
    public double LatencyMs { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public string FinishReason { get; init; } = "Unknown";
    public string ErrorMessage { get; init; } = string.Empty;
}