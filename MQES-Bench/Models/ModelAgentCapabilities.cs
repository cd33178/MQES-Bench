namespace MQESBench.Models;

/// <summary>
/// Encapsulates the discovered chat template architecture, function calling semantics,
/// active runtime stop sequences, and agent compatibility matrix for a served model.
/// </summary>
public sealed record ModelAgentCapabilities
{
    /// <summary>
    /// Inferred Jinja template architecture family (e.g., ChatML, Llama-3, DeepSeek, Phi-3 / Phi-4).
    /// </summary>
    public string TemplateFamily { get; init; } = "Unknown";

    /// <summary>
    /// Indicates whether the active Jinja template includes native function/tool execution blocks.
    /// </summary>
    public bool SupportsTools { get; init; }

    /// <summary>
    /// Inferred syntax format emitted when the model executes a tool call.
    /// </summary>
    public string ToolCallSyntax { get; init; } = "None";

    /// <summary>
    /// Active stop sequences configured in the server's runtime generation settings.
    /// </summary>
    public List<string> StopTokens { get; init; } = new();

    /// <summary>
    /// Extensible evaluation collection for all registered coding agents and assistants.
    /// </summary>
    public List<AgentCompatibility> Agents { get; init; } = new();

    // Backward-compatibility properties
    public bool OpenCodeCompatible => Agents.FirstOrDefault(a => a.Name == "OpenCode")?.IsReady ?? false;
    public bool AiderCompatible => Agents.FirstOrDefault(a => a.Name == "Aider")?.IsReady ?? false;
}