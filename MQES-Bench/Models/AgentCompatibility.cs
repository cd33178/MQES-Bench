namespace MQESBench.Models;

/// <summary>
/// Represents the compatibility assessment of an external coding agent or CLI assistant.
/// </summary>
/// <param name="Name">The display name of the agent (e.g., OpenCode, Aider, Cline).</param>
/// <param name="Status">Standardized readiness status: READY, LIMITED, BASIC, RISK, or INCOMPATIBLE.</param>
/// <param name="Details">Technical rationale explaining tokenizer, tool, or template constraints.</param>
/// <param name="IsReady">Indicates whether the agent can operate without manual prompt or template intervention.</param>
public sealed record AgentCompatibility(
    string Name,
    string Status,
    string Details,
    bool IsReady
);