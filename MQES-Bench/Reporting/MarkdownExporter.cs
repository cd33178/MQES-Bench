using System.Text;
using MQESBench.Models;

namespace MQESBench.Reporting;

/// <summary>
/// Generates the Markdown benchmark report including summary tables, category breakdown,
/// criteria analysis, and a per-test detail section with passed/failed criteria.
/// </summary>
public static class MarkdownExporter
{
    public static void Export(List<TestResult> results, ServerMetadata meta, string filePath)
    {
        var totalTokens = results.Sum(r => r.TokenCount);
        var evaluated = results.Where(r => r.Score >= 0).ToList();
        var ttfts = results.Select(r => r.TTFTMs).OrderBy(x => x).ToArray();
        var genSpeeds = results.Select(r => r.TokensPerSecond).OrderBy(x => x).ToArray();

        var cpuName = SystemTelemetry.GetProcessorName();
        var (totalRamGb, _, _, _) = SystemTelemetry.GetSystemMemoryInfo();

        var totalGenDuration = TimeSpan.FromTicks(results.Sum(r => r.GenerationDuration.Ticks));
        var totalJudgeDuration = TimeSpan.FromTicks(results.Sum(r => r.JudgeDuration.Ticks));
        var totalActiveDuration = totalGenDuration + totalJudgeDuration;

        var totalGenDurationStr = FormatDuration(totalGenDuration.TotalSeconds);
        var totalJudgeDurationStr = FormatDuration(totalJudgeDuration.TotalSeconds);
        var totalActiveDurationStr = FormatDuration(totalActiveDuration.TotalSeconds);

        var sb = new StringBuilder();
        sb.AppendLine("# LLM Benchmark Evaluation Report - llama-server (.NET 10 / C# 14)");
        sb.AppendLine($"**Execution Date:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"**Host Hardware:** `{Environment.MachineName}` | {cpuName}");
        sb.AppendLine($"**Host Memory:** {totalRamGb:F1} GB Total RAM | OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine($"**Evaluated Model:** `{meta.ModelFile}` ({meta.Quantization})");
        sb.AppendLine($"**Context Size:** `{meta.ContextSize:N0}` tokens");
        sb.AppendLine($"**Total Tests:** {results.Count}");
        sb.AppendLine($"**Total Active Time:** {totalActiveDurationStr} (Gen: {totalGenDurationStr} | Judge: {totalJudgeDurationStr})");
        sb.AppendLine($"**Total Generated Tokens:** {totalTokens:N0} (Gen: {results.Sum(r => r.GenerationTokens):N0} | Judge: {results.Sum(r => r.JudgeTokens):N0})");

        if (evaluated.Count > 0)
        {
            sb.AppendLine($"**Global Model Efficiency (MQES):** **{evaluated.Average(r => r.EfficiencyScore):F1} / 100**");
            sb.AppendLine($"**Overall Judge Quality:** {evaluated.Average(r => r.Score):F1} / 100");
            sb.AppendLine($"**Normalized Speed (Baseline):** {evaluated.Average(r => r.NormalizedTps):F2} t/s");
        }

        sb.AppendLine($"**Raw Gen Speed (Avg / P50 / P95):** {results.Average(r => r.TokensPerSecond):F2} / {MathStats.Percentile(genSpeeds, 50):F2} / {MathStats.Percentile(genSpeeds, 95):F2} t/s");
        sb.AppendLine($"**TTFT ms (Avg / P50 / P95 / P99):** {results.Average(r => r.TTFTMs):F0} / {MathStats.Percentile(ttfts, 50):F0} / {MathStats.Percentile(ttfts, 95):F0} / {MathStats.Percentile(ttfts, 99):F0} ms");
        sb.AppendLine();

        // Summary Table
        sb.AppendLine("## Performance, Resource & Score Summary\n");
        sb.AppendLine("| Category | Test Name | Quality | MQES | Raw t/s | Norm t/s | TTFT ms | Time (Gen/Judge) | Tokens (Gen/Judge) | Power | Energy | Cost USD | Status |");
        sb.AppendLine("|:---|:---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|");

        foreach (var r in results)
        {
            var status = r.Score < 0 ? "N/A" : (r.Score == 100 ? "✅ PASS" : (r.Score == 0 ? "❌ FAIL" : "⚠️ PARTIAL"));
            var qStr = r.Score < 0 ? "N/A" : $"{r.Score}/100";
            var mqesStr = r.Score < 0 ? "N/A" : $"{r.EfficiencyScore:F1}";
            var timeStr = $"{r.TotalDuration.TotalSeconds:F1}s ({r.GenerationDuration.TotalSeconds:F1}s/{r.JudgeDuration.TotalSeconds:F1}s)";
            sb.AppendLine($"| {r.Test.Category} | {r.Test.Name} | {qStr} | **{mqesStr}** | {r.TokensPerSecond:F1} | {r.NormalizedTps:F1} | {r.TTFTMs:F0} | {timeStr} | {r.TokenCount:N0} ({r.GenerationTokens:N0}/{r.JudgeTokens:N0}) | {r.Metrics.EstimatedWatts:F0} W | {r.Metrics.KWhConsumed * 1000:F2} Wh | ${r.Metrics.CostUsd:F4} | {status} |");
        }

        // Category Breakdown
        sb.AppendLine("\n## Category Breakdown\n");
        sb.AppendLine("| Category | Tests | Avg Quality | Avg MQES | Avg Raw t/s | Avg Norm t/s | Avg TTFT | PASS | PARTIAL | FAIL |");
        sb.AppendLine("|:---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|");

        foreach (var grp in results.GroupBy(r => r.Test.Category).OrderBy(g => g.Key))
        {
            var evalGrp = grp.Where(r => r.Score >= 0).ToList();
            var avgQ = evalGrp.Count > 0 ? $"{evalGrp.Average(r => r.Score):F1}" : "N/A";
            var avgMqes = evalGrp.Count > 0 ? $"{evalGrp.Average(r => r.EfficiencyScore):F1}" : "N/A";
            var pass = evalGrp.Count(r => r.Score == 100);
            var partial = evalGrp.Count(r => r.Score is > 0 and < 100);
            var fail = evalGrp.Count(r => r.Score == 0);
            sb.AppendLine($"| {grp.Key} | {grp.Count()} | {avgQ} | **{avgMqes}** | {grp.Average(r => r.TokensPerSecond):F2} | {grp.Average(r => r.NormalizedTps):F2} | {grp.Average(r => r.TTFTMs):F0} ms | {pass} | {partial} | {fail} |");
        }

        // Criteria analysis section: criteria with highest and lowest global pass rates
        var criterionStats = new Dictionary<string, (int Total, int Passed)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in evaluated)
        {
            foreach (var c in r.PassedCriteria) { criterionStats.TryGetValue(c, out var ex); criterionStats[c] = (ex.Total + 1, ex.Passed + 1); }
            foreach (var c in r.FailedCriteria) { criterionStats.TryGetValue(c, out var ex); criterionStats[c] = (ex.Total + 1, ex.Passed); }
        }

        var rankedCriteria = criterionStats
            .Where(kv => kv.Value.Total >= 2)
            .Select(kv => (Criterion: kv.Key, kv.Value.Total, kv.Value.Passed, Rate: kv.Value.Passed * 100.0 / kv.Value.Total))
            .OrderBy(x => x.Rate)
            .ToList();

        if (rankedCriteria.Count > 0)
        {
            const int topN = 5;

            var failedCriteria = rankedCriteria
                .Where(x => x.Passed < x.Total)
                .Take(topN)
                .ToList();

            var passedCriteria = rankedCriteria
                .Where(x => x.Passed > 0)
                .OrderByDescending(x => x.Rate)
                .ThenByDescending(x => x.Total)
                .Take(topN)
                .ToList();

            sb.AppendLine("\n## Criteria Pass Rate Analysis\n");

            sb.AppendLine("### ❌ Most Commonly Failed Criteria\n");
            if (failedCriteria.Count == 0)
            {
                sb.AppendLine("*None — All evaluated criteria passed with 100% success.*\n");
            }
            else
            {
                sb.AppendLine("| Criterion | Pass Rate | Passed | Total |");
                sb.AppendLine("|:---|:---:|:---:|:---:|");
                foreach (var (crit, total, passed, rate) in failedCriteria)
                {
                    sb.AppendLine($"| {crit} | **{rate:F1}%** | {passed} | {total} |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("### ✅ Most Consistently Passed Criteria\n");
            if (passedCriteria.Count == 0)
            {
                sb.AppendLine("*None — No criteria passed.*\n");
            }
            else
            {
                sb.AppendLine("| Criterion | Pass Rate | Passed | Total |");
                sb.AppendLine("|:---|:---:|:---:|:---:|");
                foreach (var (crit, total, passed, rate) in passedCriteria)
                {
                    sb.AppendLine($"| {crit} | **{rate:F1}%** | {passed} | {total} |");
                }
                sb.AppendLine();
            }
        }

        // Individual Details
        sb.AppendLine("\n## Test Details\n");
        foreach (var r in results)
        {
            var scoreStr = r.Score < 0 ? "N/A (--no-judge)" : $"{r.Score}/100";
            var mqesStr = r.Score < 0 ? "N/A" : $"{r.EfficiencyScore:F1}/100";

            sb.AppendLine($"### [{r.Test.Category}] {r.Test.Name}");
            sb.AppendLine($"- **Quality Score:** {scoreStr} | **MQES (Efficiency Score):** **{mqesStr}**");
            sb.AppendLine($"- **Duration:** {r.TotalDuration.TotalSeconds:F2}s (Generation: {r.GenerationDuration.TotalSeconds:F2}s | Judge: {r.JudgeDuration.TotalSeconds:F2}s)");
            sb.AppendLine($"- **Gen Speed:** {r.TokensPerSecond:F2} t/s | **Normalized Speed:** {r.NormalizedTps:F2} t/s | **TTFT:** {r.TTFTMs:F0} ms");
            sb.AppendLine($"- **Total Tokens:** {r.TokenCount:N0} (Gen: {r.GenerationTokens:N0} | Judge: {r.JudgeTokens:N0})");
            sb.AppendLine($"- **App CPU Usage:** {r.Metrics.ProcessCpuPct:F2}%");
            sb.AppendLine($"- **App Memory (Working Set):** {r.Metrics.ProcessWorkingSetMb:F2} MB (Managed Heap: {r.Metrics.ManagedHeapMb:F2} MB)");
            sb.AppendLine($"- **System Memory:** {r.Metrics.SystemUsedGb:F2} GB / {r.Metrics.SystemTotalGb:F2} GB ({r.Metrics.SystemLoadPct}% in use)");
            sb.AppendLine($"- **Garbage Collector Delta:** Gen0: `{r.Metrics.Gen0Collections}` | Gen1: `{r.Metrics.Gen1Collections}` | Gen2: `{r.Metrics.Gen2Collections}`");

            if (r.PassedCriteria.Count > 0)
            {
                sb.AppendLine("\n**Passed Criteria:**");
                foreach (var pass in r.PassedCriteria)
                {
                    sb.AppendLine($"  - ✅ {pass}");
                }
            }

            if (r.FailedCriteria.Count > 0)
            {
                sb.AppendLine("\n**Failed Criteria:**");
                foreach (var fail in r.FailedCriteria)
                {
                    sb.AppendLine($"  - ❌ {fail}");
                }

                sb.AppendLine("\n<details><summary>View Generated Model Output</summary>\n");
                sb.AppendLine("```csharp");
                sb.AppendLine(r.Response);
                sb.AppendLine("```");
                sb.AppendLine("\n</details>\n");
            }

            sb.AppendLine("---");
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[EXPORT] Markdown report saved to: {Path.GetFullPath(filePath)}");
        Console.ResetColor();
    }

    /// <summary>
    /// Formats elapsed seconds into an accurate, human-readable duration string.
    /// Explicitly prepends days when total duration spans 24 hours or more (e.g., "1d 04:25:20")
    /// to avoid standard TimeSpan 24-hour rollover truncation.
    /// </summary>
    /// <param name="totalSeconds">Total elapsed time expressed in seconds.</param>
    /// <returns>A formatted duration string representation.</returns>
    private static string FormatDuration(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);

        // If duration exceeds 24 hours, display explicit days: "1d 04:25:20"
        return ts.TotalDays >= 1
            ? $"{(int)ts.TotalDays}d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"; // If duration is under 24 hours: "04:25:20"

    }
}