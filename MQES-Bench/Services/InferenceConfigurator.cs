using MQESBench.Models;

namespace MQESBench.Services;

/// <summary>
/// Automatically resolves sampling and inference parameters based on the active model
/// and host hardware profile. CLI arguments always take precedence over auto-tuned values.
/// </summary>
public static class InferenceConfigurator
{
    /// <summary>
    /// Computes optimal inference parameters by combining model architecture,
    /// server context window, and available hardware.
    /// CLI overrides (cliMaxTokens, cliTemperature, cliTopP) have the highest priority.
    /// </summary>
    public static TunedParameters ResolveParameters(
        ServerMetadata serverMeta,
        HardwarePowerProfile hardwareProfile,
        int? cliMaxTokens = null,
        float? cliTemperature = null,
        float? cliTopP = null)
    {
        var modelName = (serverMeta.ModelFile ?? string.Empty).ToLowerInvariant();

        // 1. Detect model architecture and specialization.
        // Pure-coder models take precedence to avoid false positives on strings like "qwen"
        var isPureCoder = modelName.Contains("coder") ||
                          modelName.Contains("codestral") ||
                          modelName.Contains("starcoder") ||
                          modelName.Contains("dev");

        // Reasoning models with internal chain-of-thought loops (<think>...</think>)
        var isReasoningModel = !isPureCoder && (
                               modelName.Contains("qwq") ||
                               modelName.Contains("deepseek-r1") ||
                               modelName.Contains("r1") ||
                               modelName.Contains("reasoning") ||
                               modelName.Contains("qwen3.8-ud") ||
                               modelName.Contains("cot") ||
                               modelName.Contains("thinking"));

        // 2. Compute base parameters for the detected profile
        int autoTokens;
        float autoTemp;
        float autoTopP;
        string profileDesc;

        if (isPureCoder)
        {
            // Deterministic profile for code generation with strict syntax adherence
            autoTokens = 2000;
            autoTemp = 0.20f;
            autoTopP = 0.90f;
            profileDesc = "Deterministic Coder Profile (Strict Syntax, Zero-Monologue)";
        }
        else if (isReasoningModel)
        {
            // Expanded budget: ~800-1400 tokens for <think> block + ~500-800 for the solution
            autoTokens = 2500;
            autoTemp = 0.60f;
            autoTopP = 0.95f;
            profileDesc = "Reasoning/CoT Profile (Expanded Budget & Focused Sampler)";
        }
        else
        {
            // General instruct models without reasoning tokens (Llama 3, Mistral, etc.)
            autoTokens = 1500;
            autoTemp = 0.30f;
            autoTopP = 0.90f;
            profileDesc = "Standard General Instruct Profile";
        }

        // 3. Safety clamp: MaxOutputTokens must not exceed 65% of total context
        // to guarantee enough headroom for the system prompt and user prompt
        if (serverMeta.ContextSize > 0)
        {
            var maxSafeTokens = (int)(serverMeta.ContextSize * 0.65);
            if (autoTokens > maxSafeTokens)
            {
                autoTokens = Math.Max(1000, maxSafeTokens);
            }
        }

        // 4. Apply CLI overrides (highest priority)
        var finalTokens = cliMaxTokens is > 0 ? cliMaxTokens.Value : autoTokens;
        var finalTemp = cliTemperature ?? autoTemp;
        var finalTopP = cliTopP ?? autoTopP;

        return new TunedParameters(finalTokens, finalTemp, finalTopP, profileDesc);
    }
}