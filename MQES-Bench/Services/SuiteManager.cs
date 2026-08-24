using System.Text.Json;
using MQESBench.Models;

namespace MQESBench.Services;

/// <summary>
/// Loads and parses benchmark suite JSON files.
/// Supports both a flat <see cref="TestCase"/> array and a
/// <see cref="BenchmarkSuiteContainer"/> object with optional global metadata.
/// </summary>
public static class SuiteManager
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Reads and deserializes the benchmark suite from <paramref name="filePath"/>.
    /// Returns an empty container if the file does not exist or contains parse errors.
    /// </summary>
    public static BenchmarkSuiteContainer GetBenchmarkSuite(string filePath = "benchmark_suite.json")
    {
        if (!File.Exists(filePath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Benchmark suite file not found: '{filePath}'.");
            Console.ResetColor();
            return new BenchmarkSuiteContainer();
        }

        try
        {
            var jsonContent = File.ReadAllText(filePath);

            using var doc = JsonDocument.Parse(jsonContent, DocumentOptions);

            // Object format: { "Name": "...", "Tests": [...] }
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var container = JsonSerializer.Deserialize<BenchmarkSuiteContainer>(jsonContent, DeserializeOptions);
                return container ?? new BenchmarkSuiteContainer();
            }

            // Flat array format: [ { ... }, { ... } ]
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var tests = JsonSerializer.Deserialize<List<TestCase>>(jsonContent, DeserializeOptions) ?? [];
                return new BenchmarkSuiteContainer
                {
                    Name = Path.GetFileNameWithoutExtension(filePath),
                    Tests = tests
                };
            }

            return new BenchmarkSuiteContainer();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Failed to parse suite file '{filePath}': {ex.Message}");
            Console.ResetColor();
            return new BenchmarkSuiteContainer();
        }
    }

    /// <summary>
    /// Parses a range/index string (1-based) and returns the set of selected indices.
    /// Supported formats: "1,3,5" (list), "3-5" (range), "10-" (from 10 to end), "-5" (up to 5).
    /// </summary>
    public static HashSet<int> ParseIndexRanges(string input, int totalCount)
    {
        var indices = new HashSet<int>();
        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (part.EndsWith('-'))
            {
                // Open-ended range: "N-" → from N to the end
                if (int.TryParse(part[..^1], out var start))
                {
                    for (var k = Math.Max(1, start); k <= totalCount; k++) indices.Add(k);
                }
            }
            else if (part.StartsWith('-'))
            {
                // Leading range: "-N" → from 1 to N
                if (int.TryParse(part[1..], out var end))
                {
                    for (var k = 1; k <= Math.Min(end, totalCount); k++) indices.Add(k);
                }
            }
            else if (part.Contains('-'))
            {
                // Closed range: "N-M"
                var bounds = part.Split('-', StringSplitOptions.TrimEntries);
                if (bounds.Length == 2 && int.TryParse(bounds[0], out var start) && int.TryParse(bounds[1], out var end))
                {
                    var min = Math.Max(1, Math.Min(start, end));
                    var max = Math.Min(totalCount, Math.Max(start, end));
                    for (var k = min; k <= max; k++) indices.Add(k);
                }
            }
            else if (int.TryParse(part, out var singleIndex) && singleIndex >= 1 && singleIndex <= totalCount)
            {
                indices.Add(singleIndex);
            }
        }

        return indices;
    }
}