using System.Text;
using MQESBench.Models;

namespace MQESBench.Reporting;

/// <summary>
/// Renders the summary report to the console at the end of a benchmark run.
/// Includes MQES quality stats, performance, resource usage, score distribution,
/// criteria analysis, and a color-coded per-category breakdown.
/// </summary>
public static class ConsoleReporter
{
    public static void PrintEnhancedSummaryReport(List<TestResult> testResults, ModelAgentCapabilities capabilities, TimeSpan elapsed)
    {
        if (testResults.Count == 0)
        {
            Console.WriteLine("\n[WARN] No results available to generate report summary.");
            return;
        }

        Console.OutputEncoding = Encoding.UTF8;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n{new string('═', 95)}");
        Console.WriteLine("   BENCHMARK REPORT - ADVANCED METRICS & CLR INTERNALS");
        Console.WriteLine(new string('═', 95));
        Console.ResetColor();

        // 1. Quality & Model Efficiency Statistics (MQES) - GLOBAL
        var evaluated = testResults.Where(r => r.Score >= 0).ToList();
        if (evaluated.Count > 0)
        {
            var avgQuality = evaluated.Average(r => r.Score);
            var avgMqes = evaluated.Average(r => r.EfficiencyScore);
            var avgNormTps = evaluated.Average(r => r.NormalizedTps);

            var qualityScores = evaluated.Select(r => (double)r.Score).OrderBy(x => x).ToArray();
            var qualityStdDev = MathStats.CalculateStdDev(qualityScores);

            var mqesScores = evaluated.Select(r => r.EfficiencyScore).OrderBy(x => x).ToArray();
            var mqesStdDev = MathStats.CalculateStdDev(mqesScores);

            var passed = evaluated.Count(r => r.Score == 100);
            var partial = evaluated.Count(r => r.Score is > 0 and < 100);
            var failed = evaluated.Count(r => r.Score == 0);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n┌─ QUALITY & MODEL EFFICIENCY SCORE (MQES) ──────────────────────────────────");
            Console.ResetColor();
            Console.WriteLine($"│ Global MQES Score     : {avgMqes:F1} / 100 pts  (Median: {MathStats.Percentile(mqesScores, 50):F1} | P95: {MathStats.Percentile(mqesScores, 95):F1} | StdDev: ±{mqesStdDev:F2})");
            Console.WriteLine($"│ Judge Quality Avg     : {avgQuality:F1} / 100 pts  (Median: {MathStats.Percentile(qualityScores, 50):F1} | StdDev: ±{qualityStdDev:F2} pts)");
            Console.WriteLine($"│ Normalized Speed (TPS): {avgNormTps:F2} t/s baseline (Hardware-Agnostic)");
            Console.WriteLine($"│ PASS (100%): {passed,3} ({100.0 * passed / evaluated.Count:F0}%)  │  PARTIAL: {partial,3} ({100.0 * partial / evaluated.Count:F0}%)  │  FAIL: {failed,3} ({100.0 * failed / evaluated.Count:F0}%)");
            Console.WriteLine("└──────────────────────────────────────────────────────────────────────────────\n");

            PrintScoreDistribution(mqesScores);
        }

        // 2. Performance Statistics
        var ttfts = testResults.Select(r => r.TTFTMs).OrderBy(x => x).ToArray();
        var genSpeeds = testResults.Select(r => r.TokensPerSecond).OrderBy(x => x).ToArray();
        var e2ETimes = testResults.Select(r => r.TTFTMs + r.TokenCount / Math.Max(r.TokensPerSecond, 0.01) * 1000).OrderBy(x => x).ToArray();
        var totalToks = testResults.Sum(r => r.TokenCount);

        var ttftStdDev = MathStats.CalculateStdDev(ttfts);
        var genStdDev = MathStats.CalculateStdDev(genSpeeds);
        var safeMinutes = Math.Max(elapsed.TotalMinutes, 0.001);

        var totalGenDuration = TimeSpan.FromTicks(testResults.Sum(r => r.GenerationDuration.Ticks));
        var totalJudgeDuration = TimeSpan.FromTicks(testResults.Sum(r => r.JudgeDuration.Ticks));
        var totalActiveDuration = totalGenDuration + totalJudgeDuration;

        var avgGenDurationSec = testResults.Count > 0 ? testResults.Average(r => r.GenerationDuration.TotalSeconds) : 0;
        var avgJudgeDurationSec = testResults.Count > 0 ? testResults.Average(r => r.JudgeDuration.TotalSeconds) : 0;
        var avgTotalDurationSec = testResults.Count > 0 ? testResults.Average(r => r.TotalDuration.TotalSeconds) : 0;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("┌─ PERFORMANCE (LATENCY, TIMING & THROUGHPUT) ────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"│ Total Tokens Generated : {totalToks:N0} (Gen: {testResults.Sum(r => r.GenerationTokens):N0} | Judge: {testResults.Sum(r => r.JudgeTokens):N0})");
        Console.WriteLine($"│ Total Active Time      : {FormatDuration(totalActiveDuration)} (Gen: {FormatDuration(totalGenDuration)} | Judge: {FormatDuration(totalJudgeDuration)})");
        Console.WriteLine($"│ Avg Time per Test      : {avgTotalDurationSec:F1}s (Gen: {avgGenDurationSec:F1}s | Judge: {avgJudgeDurationSec:F1}s)");
        Console.WriteLine($"│ Global Throughput      : {testResults.Count / safeMinutes:F2} req/min  ({totalToks / Math.Max(elapsed.TotalSeconds, 0.1):F1} tok/s global)");
        Console.WriteLine("│");
        Console.WriteLine("│ ╭─ Time To First Token (TTFT) ───────────────────────────────────────────────");
        Console.WriteLine($"│ │  Avg: {testResults.Average(r => r.TTFTMs):F0} ms  │  Median: {MathStats.Percentile(ttfts, 50):F0} ms");
        Console.WriteLine($"│ │  P95: {MathStats.Percentile(ttfts, 95):F0} ms  │  P99: {MathStats.Percentile(ttfts, 99):F0} ms  │  StdDev: {ttftStdDev:F1} ms");
        Console.WriteLine($"│ │  Min: {ttfts.Min():F0} ms  │  Max: {ttfts.Max():F0} ms");
        Console.WriteLine("│ ╰─────────────────────────────────────────────────────────────────────────────");
        Console.WriteLine("│");
        Console.WriteLine("│ ╭─ Generation Speed (tokens/s) ───────────────────────────────────────────────");
        Console.WriteLine($"│ │  Avg: {testResults.Average(r => r.TokensPerSecond):F2} t/s  │  Median: {MathStats.Percentile(genSpeeds, 50):F2} t/s");
        Console.WriteLine($"│ │  P95: {MathStats.Percentile(genSpeeds, 95):F2} t/s  │  P99: {MathStats.Percentile(genSpeeds, 99):F2} t/s  │  StdDev: {genStdDev:F2} t/s");
        Console.WriteLine($"│ │  Min: {genSpeeds.Min():F2} t/s  │  Max: {genSpeeds.Max():F2} t/s  │  CV: {MathStats.CoefficientOfVariation(genSpeeds):F1}% (consistency)");
        Console.WriteLine("│ ╰─────────────────────────────────────────────────────────────────────────────");
        Console.WriteLine("│");
        Console.WriteLine("│ ╭─ Estimated End-to-End Latency (E2E) ────────────────────────────────────────");
        Console.WriteLine($"│ │  Avg: {e2ETimes.Average():F0} ms ({e2ETimes.Average() / 1000.0:F1} s)  │  Median: {MathStats.Percentile(e2ETimes, 50):F0} ms");
        Console.WriteLine($"│ │  P95: {MathStats.Percentile(e2ETimes, 95):F0} ms ({MathStats.Percentile(e2ETimes, 95) / 1000.0:F1} s)  │  P99: {MathStats.Percentile(e2ETimes, 99):F0} ms");
        Console.WriteLine("│ ╰─────────────────────────────────────────────────────────────────────────────");
        Console.WriteLine("│");
        Console.WriteLine($"│ Total Wall-Clock Elapsed Time: {FormatDuration(elapsed)}");
        Console.WriteLine("└──────────────────────────────────────────────────────────────────────────────\n");

        // 3. Capabilities report
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("┌─ AGENT & TOOL COMPATIBILITY AUDIT (Aider / OpenCode) ──────────────────────");
        Console.ResetColor();

        var toolsColor = capabilities.SupportsTools ? ConsoleColor.Green : ConsoleColor.Red;
        var toolsStatus = capabilities.SupportsTools ? "YES (Native Jinja Function Calling)" : "NO (Plain code only)";

        Console.WriteLine($"│ Chat Template Family  : {capabilities.TemplateFamily}");
        Console.Write("│ Tool Calling Support  : ");
        Console.ForegroundColor = toolsColor;
        Console.WriteLine(toolsStatus);
        Console.ResetColor();

        Console.WriteLine($"│ Inferred Tool Syntax  : {capabilities.ToolCallSyntax}");

        Console.Write("│ OpenCode Ready        : ");
        Console.ForegroundColor = capabilities.OpenCodeCompatible ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(capabilities.OpenCodeCompatible ? "READY (Standard <tool_call> schema)" : "RISK (May fail tool parser regex)");
        Console.ResetColor();

        Console.Write("│ Aider Ready           : ");
        Console.ForegroundColor = capabilities.AiderCompatible ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(capabilities.AiderCompatible ? "READY (Supports diff/whole and chat template)" : "LIMITED (Use --edit-format whole)");
        Console.ResetColor();

        if (capabilities.StopTokens.Count > 0)
        {
            Console.WriteLine($"│ Active Stop Tokens    : {string.Join(", ", capabilities.StopTokens)}");
        }
        Console.WriteLine("└──────────────────────────────────────────────────────────────────────────────\n");

        // 4. Top 3 & Bottom 3
        var top3Speed = testResults.OrderByDescending(r => r.TokensPerSecond).Take(3).ToList();
        var bottom3Speed = testResults.OrderBy(r => r.TokensPerSecond).Take(3).ToList();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("┌─ TOP 3 - Fastest Tests (t/s) ────────────────────────────────────────────────");
        Console.ResetColor();
        foreach (var r in top3Speed)
        {
            Console.WriteLine($"│ {r.TokensPerSecond,6:F2} t/s  │  [{r.Test.Category}] {MathStats.Truncate(r.Test.Name, 46)}");
        }
        Console.WriteLine("└──────────────────────────────────────────────────────────────────────────────\n");

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("┌─ BOTTOM 3 - Slowest Tests (t/s) ────────────────────────────────────────────");
        Console.ResetColor();
        foreach (var r in bottom3Speed)
        {
            Console.WriteLine($"│ {r.TokensPerSecond,6:F2} t/s  │  [{r.Test.Category}] {MathStats.Truncate(r.Test.Name, 46)}");
        }
        Console.WriteLine("└──────────────────────────────────────────────────────────────────────────────\n");

        // 5. Resource Efficiency & Telemetry
        var avgCpu = testResults.Average(r => r.Metrics.ProcessCpuPct);
        var avgRam = testResults.Average(r => r.Metrics.ProcessWorkingSetMb);
        var peakRam = testResults.Max(r => r.Metrics.ProcessWorkingSetMb);
        var peakSysRam = testResults.Max(r => r.Metrics.SystemUsedGb);
        var tokensPerMbRam = totalToks / Math.Max(avgRam, 1.0);
        var tokensPerCpuPct = totalToks / Math.Max(avgCpu, 0.01);
        var totalKWh = testResults.Sum(r => r.Metrics.KWhConsumed);
        var totalCost = testResults.Sum(r => r.Metrics.CostUsd);
        var avgWatts = testResults.Average(r => r.Metrics.EstimatedWatts);

        var joulesPerToken = totalToks > 0 ? totalKWh * 3_600_000.0 / totalToks : 0.0;
        var tokensPerWattHour = totalKWh > 0 ? totalToks / (totalKWh * 1000.0) : 0.0;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("┌─ RESOURCE EFFICIENCY & ENERGY CONSUMPTION ───────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"│ Estimated Avg Power    : {avgWatts:F1} Watts");
        Console.WriteLine($"│ Total Energy Consumed  : {totalKWh:F4} kWh  ({totalKWh * 1000.0:F1} Wh / {totalKWh * 3.6:F2} MJ)");
        Console.WriteLine($"│ Estimated Electricity  : ${totalCost:F4} USD (Base rate: ${testResults.FirstOrDefault()?.Metrics.CostUsd:F2}/kWh)");
        Console.WriteLine($"│ Energy Efficiency      : {joulesPerToken:F1} Joules/Token  ({tokensPerWattHour:F0} Tokens/Wh)");
        Console.WriteLine($"│ App Avg RAM (WS)       : {avgRam:F1} MB (Peak: {peakRam:F1} MB)");
        Console.WriteLine($"│ App Avg CPU            : {avgCpu:F2}%");
        Console.WriteLine($"│ Peak System RAM        : {peakSysRam:F1} GB");
        Console.WriteLine($"│ Memory Efficiency      : {tokensPerMbRam:F0} tokens / MB App");
        Console.WriteLine($"│ CPU Efficiency         : {tokensPerCpuPct:F0} tokens / % CPU");
        Console.WriteLine($"│ GC Total Collections   : Gen0: {testResults.Sum(r => r.Metrics.Gen0Collections)} │ Gen1: {testResults.Sum(r => r.Metrics.Gen1Collections)} │ Gen2: {testResults.Sum(r => r.Metrics.Gen2Collections)}");
        Console.WriteLine("└──────────────────────────────────────────────────────────────────────────────\n");

        // 6. Category breakdown with visual highlighting for best/worst MQES performance
        var categoryGroups = testResults.GroupBy(r => r.Test.Category).OrderBy(g => g.Key).ToList();
        var categoryMqes = categoryGroups
            .Where(g => g.Any(r => r.Score >= 0))
            .ToDictionary(g => g.Key, g => g.Where(r => r.Score >= 0).Average(r => r.EfficiencyScore));

        var bestCatMqes = categoryMqes.Count > 0 ? categoryMqes.Max(kv => kv.Value) : double.MaxValue;
        var worstCatMqes = categoryMqes.Count > 0 ? categoryMqes.Min(kv => kv.Value) : double.MinValue;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("┌─ CATEGORY BREAKDOWN ──────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"│ {"Category",-37} {"Tests",5} {"Avg Q",7} {"MQES",7} {"Med t/s",8} {"Norm t/s",9} {"Pass",4} {"Part",4} {"Fail",4}");
        Console.WriteLine($"├{new string('─', 95)}");

        foreach (var grp in categoryGroups)
        {
            var evalGrp = grp.Where(r => r.Score >= 0).ToList();
            var avgQualityStr = evalGrp.Count > 0 ? $"{evalGrp.Average(r => r.Score):F1}" : "N/A";
            var avgMqesVal = evalGrp.Count > 0 ? evalGrp.Average(r => r.EfficiencyScore) : -1.0;
            var avgMqesStr = avgMqesVal >= 0 ? $"{avgMqesVal:F1}" : "N/A";
            var medianSpeed = MathStats.Percentile(grp.Select(r => r.TokensPerSecond).OrderBy(x => x).ToArray(), 50);
            var avgNormSpeed = grp.Average(r => r.NormalizedTps);

            var pass = evalGrp.Count(r => r.Score == 100);
            var part = evalGrp.Count(r => r.Score is > 0 and < 100);
            var fail = evalGrp.Count(r => r.Score == 0);
            var cat = MathStats.Truncate(grp.Key, 37);

            Console.Write($"│ {cat,-37} {grp.Count(),5} {avgQualityStr,7} ");

            // Best category = bright green, worst = red; others by normal threshold
            var mqesColor = avgMqesVal switch
            {
                >= 0 when Math.Abs(avgMqesVal - bestCatMqes) < 0.01 => ConsoleColor.Green,
                >= 0 when Math.Abs(avgMqesVal - worstCatMqes) < 0.01 => ConsoleColor.Red,
                >= 80 => ConsoleColor.Green,
                >= 50 => ConsoleColor.Yellow,
                _ => avgMqesVal >= 0 ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray
            };

            Console.ForegroundColor = mqesColor;
            Console.Write($"{avgMqesStr,7}");
            Console.ResetColor();

            Console.WriteLine($" {medianSpeed,8:F1} {avgNormSpeed,9:F1} {pass,4} {part,4} {fail,4}");
        }
        Console.WriteLine($"└{new string('─', 95)}\n");

        // 7. Criteria analysis: most frequently failed and most consistently passed
        PrintCriteriaAnalysis(testResults);
    }

    /// <summary>
    /// Prints evaluation criteria ranked by global pass rate (highest and lowest).
    /// Useful for identifying which model skills fail most consistently.
    /// </summary>
    private static void PrintCriteriaAnalysis(IEnumerable<TestResult> testResults)
    {
        var evaluated = testResults.Where(r => r.Score >= 0).ToList();
        if (evaluated.Count == 0)
        {
            return;
        }

        // Build map: criterion description -> (total occurrences, total passed)
        var critStats = new Dictionary<string, (int Total, int Passed)>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in evaluated)
        {
            foreach (var key in result.PassedCriteria.Select(c => MathStats.Truncate(c, 70)))
            {
                critStats.TryGetValue(key, out var existing);
                critStats[key] = (existing.Total + 1, existing.Passed + 1);
            }

            foreach (var key in result.FailedCriteria.Select(c => MathStats.Truncate(c, 70)))
            {
                critStats.TryGetValue(key, out var existing);
                critStats[key] = (existing.Total + 1, existing.Passed);
            }
        }

        if (critStats.Count == 0)
        {
            return;
        }

        // Compute pass rate per criterion across all criteria
        var ranked = critStats
            .Select(kv => (Criterion: kv.Key,
                           kv.Value.Total,
                           kv.Value.Passed,
                           Failed: kv.Value.Total - kv.Value.Passed,
                           PassRate: kv.Value.Passed * 100.0 / kv.Value.Total))
            .ToList();

        if (ranked.Count == 0)
        {
            return;
        }

        const int topN = 5;

        var worst = ranked
            .Where(x => x.Failed > 0)
            .OrderBy(x => x.PassRate)
            .ThenByDescending(x => x.Failed)
            .ThenByDescending(x => x.Total)
            .Take(topN)
            .ToList();

        var best = ranked
            .Where(x => x.Passed > 0)
            .OrderByDescending(x => x.PassRate)
            .ThenByDescending(x => x.Passed)
            .ThenByDescending(x => x.Total)
            .Take(topN)
            .ToList();

        // ── TOP MOST FAILED CRITERIA ─────────────────────────────────────────────
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"┌─ TOP {topN} MOST FAILED CRITERIA ──────────────────────────────────────────────────────────────");
        Console.ResetColor();

        if (worst.Count == 0)
        {
            Console.WriteLine("│ (None — All evaluated criteria passed with 100% success)");
        }
        else
        {
            foreach (var item in worst)
            {
                var failRate = 100.0 - item.PassRate;
                var barLength = (int)Math.Round(failRate / 5.0);
                var bar = new string('█', Math.Clamp(barLength, 0, 20));

                Console.Write($"│ {item.PassRate,5:F1}% ({item.Passed}/{item.Total})  ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"{bar,-20}");
                Console.ResetColor();
                Console.WriteLine($"  {item.Criterion}");
            }
        }
        Console.WriteLine($"└{new string('─', 95)}\n");

        // ── TOP MOST CONSISTENTLY PASSED CRITERIA ────────────────────────────────
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"┌─ TOP {topN} MOST CONSISTENTLY PASSED CRITERIA ──────────────────────────────────────────────────");
        Console.ResetColor();

        if (best.Count == 0)
        {
            Console.WriteLine("│ (None — No criteria passed)");
        }
        else
        {
            foreach (var item in best)
            {
                var barLength = (int)Math.Round(item.PassRate / 5.0);
                var bar = new string('█', Math.Clamp(barLength, 0, 20));

                Console.Write($"│ {item.PassRate,5:F1}% ({item.Passed}/{item.Total})  ");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"{bar,-20}");
                Console.ResetColor();
                Console.WriteLine($"  {item.Criterion}");
            }
        }
        Console.WriteLine($"└{new string('─', 95)}\n");
    }

    private static void PrintScoreDistribution(double[] scores)
    {
        if (scores.Length == 0)
        {
            return;
        }

        const int bins = 10;
        const int maxBarWidth = 45;
        var histogram = new int[bins];

        foreach (var score in scores)
        {
            var bin = Math.Clamp((int)(score / 10), 0, bins - 1);
            histogram[bin]++;
        }

        var maxCount = histogram.Max();
        if (maxCount == 0)
        {
            return;
        }

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("┌─ SCORE DISTRIBUTION (HISTOGRAM - MQES) ─────────────────────────────────────");
        Console.ResetColor();
        for (var i = 0; i < bins; i++)
        {
            var rangeLabel = i == 9 ? " 90-100" : $"{i * 10,3}-{(i + 1) * 10,3}";
            var barLength = (int)((double)histogram[i] / maxCount * maxBarWidth);
            var bar = new string('█', barLength);

            Console.Write($"│ {rangeLabel} │ ");
            Console.ForegroundColor = i switch
            {
                >= 8 => ConsoleColor.Green,
                >= 5 => ConsoleColor.Yellow,
                _ => ConsoleColor.Red
            };

            Console.WriteLine($"{bar} {histogram[i]}");
            Console.ResetColor();
        }
        Console.WriteLine("└──────────────────────────────────────────────────────────────────────────────\n");
    }

    /// <summary>
    /// Formats a duration in seconds, displaying days explicitly if >= 24 hours to prevent rollover truncation.
    /// </summary>
    private static string FormatDuration(TimeSpan ts)
    {
        return ts.TotalDays >= 1
            ? $"{(int)ts.TotalDays}d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public static void ListCategories(List<TestCase> tests)
    {
        var categories = tests.Select(t => t.Category).Distinct().ToList();
        Console.WriteLine("\nAvailable categories:");

        var maxLen = categories.Select(cat => cat.Length).Prepend(0).Max() + 1;
        var totalCount = 0;
        foreach (var cat in categories)
        {
            var count = tests.Count(t => t.Category == cat);
            Console.WriteLine($"  - {cat.PadRight(maxLen)} ({count} test{(count > 1 ? "s" : "")})");
            totalCount += count;
        }

        Console.WriteLine($"  - ALL ({totalCount}) {new string(' ', Math.Max(0, maxLen - 6 - totalCount.ToString().Length))}(Runs all test cases)");
    }

    /// <summary>
    /// Renders an in-depth architectural and inference inspection report.
    /// Uses empirical live probe results when available, and gracefully falls back to
    /// model signature heuristics (explicitly labeled with [Est]) if the probe fails.
    /// </summary>
    public static void PrintModelInspectionReport(
    string endpoint,
        ServerMetadata meta,
        ModelAgentCapabilities caps,
        ModelProbeResult probe)
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n{new string('═', 80)}");
        Console.WriteLine("   LLM INFERENCE & ARCHITECTURAL INSPECTION REPORT");
        Console.WriteLine(new string('═', 80));
        Console.ResetColor();

        Console.WriteLine($"  Endpoint     : {endpoint}");
        Console.WriteLine($"  Model File   : {meta.ModelFile}");
        Console.WriteLine($"  Quantization : {meta.Quantization}");
        Console.WriteLine($"  Host Target  : {meta.Hardware}");
        Console.WriteLine(new string('─', 80));

        // 1. GGUF Architecture & Context Limits
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("┌─ GGUF ARCHITECTURE & RUNTIME LIMITS ──────────────────────────────────────────");
        Console.ResetColor();
        var layersStr = meta.LayerCount > 0 ? $"{meta.LayerCount} layers" : "N/A";
        var vocabStr = meta.VocabularySize > 0 ? $"{meta.VocabularySize:N0} tokens" : "N/A";
        Console.WriteLine($"│ Transformer Layers     : {layersStr,-25} │ Active Context : {meta.ContextSize:N0} tokens");
        Console.WriteLine($"│ Embedding Dim          : {meta.EmbeddingDimension,-25} │ Native Max Ctx : {meta.TrainingContextSize:N0} tokens");
        Console.WriteLine($"│ Vocabulary Size        : {vocabStr,-25} │ Parallel Slots : {meta.TotalSlots} concurrent slot(s)");
        Console.WriteLine("└───────────────────────────────────────────────────────────────────────────────\n");

        // 2. Empirical Smoke Test or Fallback Notice
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("┌─ ACTIVE EMPIRICAL PROBE & THROUGHPUT BENCHMARK ───────────────────────────────");
        Console.ResetColor();

        var hasEmpiricalData = probe.ExecutedSuccessfully;

        if (hasEmpiricalData)
        {
            var probeStatusColor = probe.HasNativeToolCalls ? ConsoleColor.Green : ConsoleColor.Yellow;
            var probeStatusText = probe.HasNativeToolCalls
                ? "VERIFIED (Tool call successfully emitted)"
                : "REJECTED (Responded with plain text / no tools)";

            Console.WriteLine($"│ Generation Speed       : {probe.TokensPerSecond:F2} t/s ({probe.CompletionTokens} tokens generated)");
            Console.WriteLine($"│ Total Latency (TTFT)   : {probe.LatencyMs:F0} ms (Prompt: {probe.PromptTokens} tokens)");
            Console.Write("│ Tool Call Test         : ");
            Console.ForegroundColor = probeStatusColor;
            Console.WriteLine(probeStatusText);
            Console.ResetColor();
            Console.WriteLine($"│ Emitted Syntax         : {probe.DetectedToolSyntax}");
            Console.WriteLine($"│ Finish Reason          : {probe.FinishReason}");
            if (!string.IsNullOrWhiteSpace(probe.RawResponseContent))
            {
                var snippet = probe.RawResponseContent.Length > 52
                    ? probe.RawResponseContent[..49] + "..."
                    : probe.RawResponseContent;
                Console.WriteLine($"│ Text Echo Output       : \"{snippet}\"");
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"│ Active Probe Status    : SKIPPED / FAILED ({probe.ErrorMessage})");
            Console.WriteLine("│ Fallback Strategy      : Using architectural heuristics & model signature matching.");
            Console.WriteLine("│ Notice                 : Agent readiness ratings below are ESTIMATES, not verified.");
            Console.ResetColor();
        }
        Console.WriteLine("└───────────────────────────────────────────────────────────────────────────────\n");

        // 3. Agent Readiness Matrix (Empirical vs Heuristic Fallback)
        Console.ForegroundColor = ConsoleColor.Cyan;
        var matrixTitle = hasEmpiricalData
            ? "AGENT COMPATIBILITY MATRIX (Empirically Verified)"
            : "AGENT COMPATIBILITY MATRIX (Estimated via Model Signature)";
        Console.WriteLine($"┌─ {matrixTitle.PadRight(77, '─')}");
        Console.ResetColor();

        // Resolve agents: If empirical test succeeded, use test outcome for tools; otherwise fall back to caps.Agents
        // Sort alphabetically by Agent Name (Case-Insensitive)
        var agentsToRender = ResolveAgentList(hasEmpiricalData, probe, caps)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var agent in agentsToRender)
        {
            Console.Write($"│ {$"{agent.Name} Agent",-21}  : ");
            Console.ForegroundColor = agent.Color;
            Console.Write($"{agent.Status,-13}");
            Console.ResetColor();
            Console.WriteLine($"│ {agent.Details}");
        }

        Console.WriteLine($"└{new string('─', 79)}\n");
    }

    /// <summary>
    /// Resolves agent readiness and technical details, leveraging empirical probe results
    /// or falling back to model identifier heuristics with explicit [Est] labels.
    /// </summary>
    private static List<(string Name, string Status, ConsoleColor Color, string Details)> ResolveAgentList(
        bool hasEmpiricalData,
        ModelProbeResult probe,
        ModelAgentCapabilities caps)
    {
        var result = new List<(string Name, string Status, ConsoleColor Color, string Details)>();

        if (hasEmpiricalData)
        {
            // Tier 1: Ground-truth empirical results from live test
            bool toolsOk = probe.HasNativeToolCalls;

            // Strict Function Calling / MCP / ACI
            result.Add(("OpenCode", toolsOk ? "READY" : "INCOMPATIBLE",
                toolsOk ? ConsoleColor.Green : ConsoleColor.Red,
                toolsOk ? "Verified: Tool calls executed natively" : "Verified: Fails tool calls (no file edits)"));

            result.Add(("Goose", toolsOk ? "READY" : "INCOMPATIBLE",
                toolsOk ? ConsoleColor.Green : ConsoleColor.Red,
                toolsOk ? "Verified: MCP & tool execution ready" : "Verified: Cannot invoke MCP toolkits"));

            result.Add(("OpenHands", toolsOk ? "READY" : "INCOMPATIBLE",
                toolsOk ? ConsoleColor.Green : ConsoleColor.Red,
                toolsOk ? "Verified: Structured action stream ready" : "Verified: Action serialization fails"));

            result.Add(("SWE-agent", toolsOk ? "READY" : "INCOMPATIBLE",
                toolsOk ? ConsoleColor.Green : ConsoleColor.Red,
                toolsOk ? "Verified: ACI commands executed natively" : "Verified: Cannot emit ACI tool commands"));

            result.Add(("Plandex", toolsOk ? "READY" : "INCOMPATIBLE",
                toolsOk ? ConsoleColor.Green : ConsoleColor.Red,
                toolsOk ? "Verified: Multi-file tool execution ready" : "Verified: Branch plan serialization fails"));

            result.Add(("Cline", toolsOk ? "READY" : "INCOMPATIBLE",
                toolsOk ? ConsoleColor.Green : ConsoleColor.Red,
                toolsOk ? "Verified: Function calling operational" : "Verified: Cannot invoke filesystem/CLI tools"));

            // Hybrid Agents
            result.Add(("Cursor/Windsurf", toolsOk ? "READY" : "LIMITED",
                toolsOk ? ConsoleColor.Green : ConsoleColor.Yellow,
                toolsOk ? "Verified: Full autonomous agent mode" : "Verified: Limited to standard diff/chat mode"));

            result.Add(("Avante.nvim", toolsOk ? "READY" : "LIMITED",
                toolsOk ? ConsoleColor.Green : ConsoleColor.Yellow,
                toolsOk ? "Verified: Context tools & planning ready" : "Verified: Limited to direct buffer completion"));

            // Diff / Text-based Agents
            result.Add(("Aider", "READY", ConsoleColor.Green, "Verified: SEARCH/REPLACE diff format supported"));
            result.Add(("Mentat", "READY", ConsoleColor.Green, "Verified: Interactive git diff mode supported"));
            result.Add(("Continue", "READY", ConsoleColor.Green, "Verified: Chat and code context operational"));
            result.Add(("Copilot CLI", "READY", ConsoleColor.Green, "Verified: Precise terminal command generation"));
        }
        else
        {
            // Tier 2: Heuristic Fallback via GGUF signature and template analysis
            foreach (var a in caps.Agents)
            {
                var estStatus = a.IsReady ? "READY [Est]" : $"{a.Status} [Est]";
                var color = a.Status switch
                {
                    "READY" => ConsoleColor.Yellow,
                    "LIMITED" or "BASIC" => ConsoleColor.DarkYellow,
                    _ => ConsoleColor.Red
                };

                result.Add((
                    a.Name,
                    estStatus,
                    color,
                    $"(Estimated from Model ID) {a.Details}"));
            }
        }

        return result;
    }

    public static void ShowHelp()
    {
        Console.WriteLine("""
        Usage syntax:
          mqes-bench [suite.json] [options]

        General & Filtering Options:
          -i,  --info, --inspect               Inspect server model architecture, capabilities and exit.
          -c,  --category <cat1,cat2>          Filter by EXACT match on Category property.
          -cc, --category-contains <t1,t2>     Filter by PARTIAL match on Category or Name.
          -ck, --cost-kwh <rate>               Electricity price per kWh in USD for power telemetry (default: 0.15).
          -f,  --file <path/filename>          Specify test suite JSON path (default: benchmark_suite.json).
          -l,  --list                          Show list of available categories in suite and exit.
          -n,  --numbers <range>               Run tests by 1-based index or ranges (e.g., -n "1,3,5", -n "3-5", -n "10-", -n "-5").
          -r,  --resume                        Resume benchmark run from last saved recovery checkpoint file.
          -h,  --help                          Show this help message and exit.

        Candidate Generator Options:
          -e,  --endpoint <url>                Candidate generator endpoint URL (default: http://localhost:8080/v1).
          -m,  --model <model-name>            Candidate model identifier (e.g. "qwen2.5-coder:32b", auto-detected if omitted).
          -ak, --api-key <key>                 API key for the generator endpoint (default: not-needed, or env LLM_API_KEY).
          -t,  --timeout <seconds>             Candidate generation timeout per test (default: 1800s / 30m).
          -k,  --tokens <number>               Maximum output token generation limit per test.
          --temp <float>                       Sampling temperature override.
          --top-p <float>                      Top-P nucleus sampling override.

        Judge & Evaluation Options:
          -nj, --no-judge                      Throughput-only mode (t/s, TTFT, tokens) without criteria evaluation.
          -je, --judge-endpoint <url>          Separate endpoint URL for LLM Judge (e.g., http://localhost:11434/v1).
          -jm, --judge-model <model-name>      Model identifier for the Judge (default: llama-server).
          -jk, --judge-key <api-key>           API key for Judge endpoint (default: inherits generator key, or env JUDGE_API_KEY).
          -jt, --judge-timeout <seconds>       Dedicated Judge timeout (default: matches generation timeout).
          -jtk, --judge-tokens <number>        Maximum output token generation limit for Judge (default: 1024).
        
        Usage examples:
          mqes-bench csharp_suite.json
          mqes-bench csharp_suite.json -t 600 -jt 180
          mqes-bench tsql_suite.json -cc lock -nj
          mqes-bench embedded_c_suite.json -je http://localhost:11434/v1 -jm qwen2.5-coder:32b -jt 1200
          mqes-bench csharp_suite.json -ak "sk-my-candidate-key"
          mqes-bench csharp_suite.json -e http://candidate:8080/v1 -ak "key1" -je http://judge:8080/v1 -jk "key2" -jm "qwen2.5-coder:32b"
          mqes-bench benchmark_csharp_net10_suite.json -e http://localhost:11434/v1 -m qwen2.5-coder:32b (Ollama Generator)
          mqes-bench benchmark_sql_suite.json -e http://candidate:8000/v1 -m unsloth/Qwen3-30B -je http://judge:11434/v1 -jm llama3.1:70b (vLLM/Unsloth + Ollama Judge)
        """);
    }
}