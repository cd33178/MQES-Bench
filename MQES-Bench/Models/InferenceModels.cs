namespace MQESBench.Models;

/// <summary>
/// Inference parameters resolved automatically from the active model and hardware profile,
/// or overridden via CLI flags. Contains sampling values, token limit, and a profile description.
/// </summary>
public readonly record struct TunedParameters(
    int MaxOutputTokens,
    float Temperature,
    float TopP,
    string ProfileDescription
);
