namespace MQESBench.Models;

/// <summary>
/// Complete result for a single test: model response, performance metrics,
/// quality scores, and system resource telemetry.
/// </summary>
public sealed record TestResult(
    TestCase Test,
    string Response,
    double TokensPerSecond,
    double TTFTMs,
    int Score,
    double EfficiencyScore,
    double NormalizedTps,
    List<string> PassedCriteria,
    List<string> FailedCriteria,
    int TokenCount,
    TestResourceMetrics Metrics,
    int GenerationTokens = 0,
    int JudgeTokens = 0,
    TimeSpan GenerationDuration = default,
    TimeSpan JudgeDuration = default
)
{
    /// <summary>Total test duration (generation + judge evaluation).</summary>
    public TimeSpan TotalDuration => GenerationDuration + JudgeDuration;
}