using MQESBench.Models;

namespace MQESBench.Scoring;

/// <summary>
/// Computes the MQES (Model Quality &amp; Efficiency Score), a composite metric that
/// balances criteria quality, information density, and hardware-normalized throughput.
/// </summary>
public static class ModelScorer
{
    /// <summary>
    /// Computes the MQES and hardware-normalized speed for a test result.
    /// <para>Weighting: 70% criteria quality · 15% token density · 15% normalized speed.</para>
    /// </summary>
    /// <param name="qualityScore">0-100 score from the criteria judge.</param>
    /// <param name="rawTps">Measured generation speed in tokens/s.</param>
    /// <param name="genTokens">Total tokens generated in the response.</param>
    /// <param name="profile">Host hardware profile used to normalize speed.</param>
    public static (double EfficiencyScore, double NormalizedTps) Calculate(
        int qualityScore,
        double rawTps,
        int genTokens,
        HardwarePowerProfile profile)
    {
        if (qualityScore <= 0)
        {
            return (0.0, 0.0);
        }

        // 1. Hardware-normalized speed (host-agnostic)
        var normalizedTps = rawTps / Math.Max(profile.HardwareCapacityFactor, 0.1);

        // Centered at 1.0x for a standard dense 32B model on baseline hardware
        var speedMultiplier = Math.Clamp(normalizedTps / 2.0, 0.6, 1.4);

        // 2. Response density / conciseness factor
        var tokenEfficiency = genTokens switch
        {
            <= 150 => 0.85,                     // Too short — light penalty
            > 150 and <= 800 => 1.15,            // Optimal conciseness zone
            > 800 and <= 1500 => 1.00,           // Long but acceptable
            _ => Math.Max(0.70, 1.0 - ((genTokens - 1500) / 4000.0))  // Progressive verbosity penalty
        };

        // 3. Final composite MQES score
        var compositeFactor = 0.70 + (0.15 * tokenEfficiency) + (0.15 * speedMultiplier);
        var finalScore = Math.Clamp(qualityScore * compositeFactor, 0.0, 100.0);

        return (Math.Round(finalScore, 1), Math.Round(normalizedTps, 2));
    }
}