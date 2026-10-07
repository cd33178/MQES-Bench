namespace MQESBench.Models;

public record ChatTemplateCaps
{
    public bool SupportsTools { get; init; }
    public bool SupportsToolCalls { get; init; }
    public bool SupportsParallelToolCalls { get; init; }
    public bool SupportsObjectArguments { get; init; }
    public bool SupportsTypedContent { get; init; }
    public bool SupportsStringContent { get; init; }
    public bool SupportsSystemRole { get; init; }
    public bool SupportsPreserveReasoning { get; init; }
    public bool SupportsReasoningEffort { get; init; }

    public static ChatTemplateCaps FromJsonElement(System.Text.Json.JsonElement element)
    {
        return new ChatTemplateCaps
        {
            SupportsTools = GetBool("supports_tools"),
            SupportsToolCalls = GetBool("supports_tool_calls"),
            SupportsParallelToolCalls = GetBool("supports_parallel_tool_calls"),
            SupportsObjectArguments = GetBool("supports_object_arguments"),
            SupportsTypedContent = GetBool("supports_typed_content"),
            SupportsStringContent = GetBool("supports_string_content"),
            SupportsSystemRole = GetBool("supports_system_role"),
            SupportsPreserveReasoning = GetBool("supports_preserve_reasoning"),
            SupportsReasoningEffort = GetBool("supports_reasoning_effort")
        };

        bool GetBool(string prop) =>
            element.TryGetProperty(prop, out var val) && val.ValueKind == System.Text.Json.JsonValueKind.True;
    }
}