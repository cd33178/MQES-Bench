namespace MQESBench.Reporting;

/// <summary>
/// Statistical utilities: percentiles, standard deviation, coefficient of variation,
/// and formatting helpers used by <see cref="ConsoleReporter"/> and the exporters.
/// </summary>
public static class MathStats
{
    /// <summary>Computes the population standard deviation of the given values.</summary>
    public static double CalculateStdDev(double[] values)
    {
        if (values.Length <= 1)
        {
            return 0;
        }

        var avg = values.Average();
        var sumSquares = values.Sum(v => Math.Pow(v - avg, 2));

        return Math.Sqrt(sumSquares / values.Length);
    }

    /// <summary>
    /// Coefficient of Variation (CV = σ/μ × 100), expressed as a percentage.
    /// Measures relative consistency: lower CV = less variability = more stable.
    /// Returns 0 when the mean is near zero to avoid division by zero.
    /// </summary>
    public static double CoefficientOfVariation(double[] values)
    {
        if (values.Length <= 1)
        {
            return 0;
        }

        var avg = values.Average();
        if (Math.Abs(avg) < 1e-9)
        {
            return 0;
        }

        return CalculateStdDev(values) / avg * 100.0;
    }

    /// <summary>
    /// Computes the <paramref name="p"/>th percentile using linear interpolation on a sorted array.
    /// </summary>
    public static double Percentile(double[] sorted, int p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var index = p / 100.0 * (sorted.Length - 1);
        var lower = (int)index;
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        var frac = index - lower;

        return sorted[lower] + frac * (sorted[upper] - sorted[lower]);
    }

    /// <summary>Truncates text to <paramref name="maxLength"/> characters, appending "..." if cut.</summary>
    public static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength
            ? text
            : $"{text.AsSpan(0, maxLength - 3)}...";
    }
}