namespace MQESBench.Models;

/// <summary>
/// Individual evaluation criterion with its weight in the total score.
/// The sum of all weights in a <see cref="TestCase"/> should equal 100.
/// </summary>
public sealed record EvaluationCriterion(string Description, int Weight = 25);

/// <summary>
/// A benchmark test case with its prompt, category, and list of evaluation criteria.
/// Optional system/suffix/judge fields override the suite-level defaults for this test only.
/// </summary>
public sealed record TestCase(
    string Name,
    string Category,
    string Prompt,
    List<EvaluationCriterion> Criteria,
    string? SystemPrompt = null,
    string? UserPromptSuffix = null,
    string? JudgeSystemPrompt = null   // Overrides global judge prompt for this test only
);

/// <summary>
/// Container for a benchmark suite JSON file. Supports both a flat <see cref="TestCase"/> array
/// and an object with global metadata (name, system prompt, user suffix, judge instructions).
/// </summary>
public sealed record BenchmarkSuiteContainer
{
    public string? Name { get; set; }
    public string? DefaultSystemPrompt { get; set; }
    public string? DefaultUserPromptSuffix { get; set; }
    public string? DefaultJudgeSystemPrompt { get; set; }
    public List<TestCase> Tests { get; set; } = [];
}