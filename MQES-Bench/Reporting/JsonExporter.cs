using MQESBench.Models;
using System.Runtime;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MQESBench.Reporting;

/// <summary>
/// Exports the full benchmark report as a structured JSON file.
/// Includes global metrics, per-category breakdown, and criteria pass rate analysis.
/// </summary>
public static class JsonExporter
{
    public static void Export(List<TestResult> results, ServerMetadata meta, string filePath)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var totalToks = results.Sum(r => r.TokenCount);
        var evaluated = results.Where(r => r.Score >= 0).ToList();
        var mqesScores = evaluated.Select(r => r.EfficiencyScore).OrderBy(x => x).ToArray();
        var ttfts = results.Select(r => r.TTFTMs).OrderBy(x => x).ToArray();
        var genSpeeds = results.Select(r => r.TokensPerSecond).OrderBy(x => x).ToArray();
        var totalKWh = results.Sum(r => r.Metrics.KWhConsumed);
        var totalCost = results.Sum(r => r.Metrics.CostUsd);
        var (totalRam, _, _, _) = SystemTelemetry.GetSystemMemoryInfo();

        var totalGenDuration = TimeSpan.FromTicks(results.Sum(r => r.GenerationDuration.Ticks));
        var totalJudgeDuration = TimeSpan.FromTicks(results.Sum(r => r.JudgeDuration.Ticks));
        var totalActiveDuration = totalGenDuration + totalJudgeDuration;

        var categoryStats = results
            .GroupBy(r => r.Test.Category)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var evalG = g.Where(r => r.Score >= 0).ToList();
                var gTtfts = g.Select(r => r.TTFTMs).OrderBy(x => x).ToArray();
                var gGen = g.Select(r => r.TokensPerSecond).OrderBy(x => x).ToArray();
                return new
                {
                    Category = g.Key,
                    TestCount = g.Count(),
                    AverageQualityScore = evalG.Count > 0 ? Math.Round(evalG.Average(r => r.Score), 1) : (double?)null,
                    AverageMqesScore = evalG.Count > 0 ? Math.Round(evalG.Average(r => r.EfficiencyScore), 1) : (double?)null,
                    PassCount = evalG.Count(r => r.Score == 100),
                    PartialCount = evalG.Count(r => r.Score > 0 && r.Score < 100),
                    FailCount = evalG.Count(r => r.Score == 0),
                    AvgGenTps = Math.Round(g.Average(r => r.TokensPerSecond), 2),
                    P50GenTps = Math.Round(MathStats.Percentile(gGen, 50), 2),
                    AvgNormTps = Math.Round(g.Average(r => r.NormalizedTps), 2),
                    AvgTtftMs = Math.Round(g.Average(r => r.TTFTMs), 1),
                    P50TtftMs = Math.Round(MathStats.Percentile(gTtfts, 50), 1),
                    TotalTokens = g.Sum(r => r.TokenCount),
                    TotalDurationSec = Math.Round(g.Sum(r => r.TotalDuration.TotalSeconds), 2),
                    TotalEnergyWh = Math.Round(g.Sum(r => r.Metrics.KWhConsumed) * 1000.0, 2),
                    TotalCostUsd = Math.Round(g.Sum(r => r.Metrics.CostUsd), 4)
                };
            });

        var exportData = new
        {
            Timestamp = DateTime.Now,
            Host = new
            {
                Environment.MachineName,
                Processor = SystemTelemetry.GetProcessorName(),
                LogicalCores = Environment.ProcessorCount,
                TotalRamGb = Math.Round(totalRam, 1),
                Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                DotNetRuntime = Environment.Version.ToString(),
                GCSettings.IsServerGC
            },
            Model = new
            {
                meta.ModelFile,
                meta.Quantization,
                meta.ContextSize
            },
            Summary = new
            {
                TotalTests = results.Count,
                TotalTokens = totalToks,
                TotalGenTokens = results.Sum(r => r.GenerationTokens),
                TotalJudgeTokens = results.Sum(r => r.JudgeTokens),
                TotalActiveDurationSec = Math.Round(totalActiveDuration.TotalSeconds, 2),
                TotalGenDurationSec = Math.Round(totalGenDuration.TotalSeconds, 2),
                TotalJudgeDurationSec = Math.Round(totalJudgeDuration.TotalSeconds, 2),
                GlobalMqes = evaluated.Count > 0 ? Math.Round(evaluated.Average(r => r.EfficiencyScore), 1) : (double?)null,
                GlobalMqesMedian = evaluated.Count > 0 ? Math.Round(MathStats.Percentile(mqesScores, 50), 1) : (double?)null,
                GlobalQualityAverage = evaluated.Count > 0 ? Math.Round(evaluated.Average(r => r.Score), 1) : (double?)null,
                GlobalNormalizedTpsAvg = evaluated.Count > 0 ? Math.Round(evaluated.Average(r => r.NormalizedTps), 2) : (double?)null,
                PassCount = evaluated.Count(r => r.Score == 100),
                PartialCount = evaluated.Count(r => r.Score > 0 && r.Score < 100),
                FailCount = evaluated.Count(r => r.Score == 0),
                GenTpsAvg = Math.Round(results.Average(r => r.TokensPerSecond), 2),
                GenTpsP50 = Math.Round(MathStats.Percentile(genSpeeds, 50), 2),
                GenTpsP95 = Math.Round(MathStats.Percentile(genSpeeds, 95), 2),
                TtftMsAvg = Math.Round(results.Average(r => r.TTFTMs), 1),
                TtftMsP50 = Math.Round(MathStats.Percentile(ttfts, 50), 1),
                TtftMsP95 = Math.Round(MathStats.Percentile(ttfts, 95), 1),
                AvgAppWorkingSetMb = Math.Round(results.Average(r => r.Metrics.ProcessWorkingSetMb), 1),
                PeakAppWorkingSetMb = Math.Round(results.Max(r => r.Metrics.ProcessWorkingSetMb), 1),
                Energy = new
                {
                    AverageWatts = Math.Round(results.Average(r => r.Metrics.EstimatedWatts), 1),
                    TotalKWh = Math.Round(totalKWh, 5),
                    TotalCostUsd = Math.Round(totalCost, 4),
                    JoulesPerToken = Math.Round(totalToks > 0 ? (totalKWh * 3_600_000.0) / totalToks : 0, 2),
                    TokensPerWattHour = Math.Round(totalKWh > 0 ? totalToks / (totalKWh * 1000.0) : 0, 1)
                },
                // Global pass rate per criterion (only criteria with >= 2 occurrences).
                // Useful for identifying which model skills fail most consistently.
                CriterionPassRates = BuildCriterionPassRates(results)
            },
            CategoryStats = categoryStats,
            Results = results.Select(r => new
            {
                r.Test.Category,
                r.Test.Name,
                QualityScore = r.Score < 0 ? (int?)null : r.Score,
                MqesScore = r.Score < 0 ? (double?)null : r.EfficiencyScore,
                GenTps = Math.Round(r.TokensPerSecond, 2),
                NormalizedTps = r.NormalizedTps,
                TtftMs = Math.Round(r.TTFTMs, 1),
                TotalTokens = r.TokenCount,
                r.GenerationTokens,
                r.JudgeTokens,
                GenerationDurationSec = Math.Round(r.GenerationDuration.TotalSeconds, 2),
                JudgeDurationSec = Math.Round(r.JudgeDuration.TotalSeconds, 2),
                TotalDurationSec = Math.Round(r.TotalDuration.TotalSeconds, 2),
                Telemetry = new
                {
                    ProcessCpuPct = Math.Round(r.Metrics.ProcessCpuPct, 2),
                    WorkingSetMb = Math.Round(r.Metrics.ProcessWorkingSetMb, 2),
                    ManagedHeapMb = Math.Round(r.Metrics.ManagedHeapMb, 2),
                    SystemUsedGb = Math.Round(r.Metrics.SystemUsedGb, 2),
                    SystemTotalGb = Math.Round(r.Metrics.SystemTotalGb, 2),
                    r.Metrics.SystemLoadPct,
                    EstimatedWatts = Math.Round(r.Metrics.EstimatedWatts, 1),
                    KWhConsumed = Math.Round(r.Metrics.KWhConsumed, 6),
                    CostUsd = Math.Round(r.Metrics.CostUsd, 5),
                    GcCollections = new
                    {
                        Gen0 = r.Metrics.Gen0Collections,
                        Gen1 = r.Metrics.Gen1Collections,
                        Gen2 = r.Metrics.Gen2Collections
                    }
                },
                r.PassedCriteria,
                r.FailedCriteria,
                r.Response
            })
        };

        var json = JsonSerializer.Serialize(exportData, options);
        File.WriteAllText(filePath, json, Encoding.UTF8);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[EXPORT] JSON report saved to: {Path.GetFullPath(filePath)}");
        Console.ResetColor();
    }

    /// <summary>
    /// Builds an ordered map of criterion → global pass rate (%).
    /// Only includes criteria that appear in at least 2 evaluated results.
    /// </summary>
    private static IEnumerable<object> BuildCriterionPassRates(List<TestResult> results)
    {
        var stats = new Dictionary<string, (int Total, int Passed)>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in results.Where(r => r.Score >= 0))
        {
            foreach (var c in r.PassedCriteria)
            {
                stats.TryGetValue(c, out var existing);
                stats[c] = (existing.Total + 1, existing.Passed + 1);
            }
            foreach (var c in r.FailedCriteria)
            {
                stats.TryGetValue(c, out var existing);
                stats[c] = (existing.Total + 1, existing.Passed);
            }
        }

        return stats
            .Where(kv => kv.Value.Total >= 2)
            .OrderBy(kv => kv.Value.Passed * 100.0 / kv.Value.Total)
            .Select(kv => (object)new
            {
                Criterion = kv.Key,
                kv.Value.Total,
                kv.Value.Passed,
                PassRatePct = Math.Round(kv.Value.Passed * 100.0 / kv.Value.Total, 1)
            });
    }
}