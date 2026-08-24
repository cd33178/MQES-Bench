using System.Text.Encodings.Web;
using System.Text.Json;
using MQESBench.Models;

namespace MQESBench.Services;

/// <summary>
/// Saves and restores benchmark progress state to a temporary JSON file.
/// Allows interrupted runs to be resumed with <c>--resume</c>.
/// </summary>
public static class CheckpointManager
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Serializes the current results to disk safely (silent failure on error).</summary>
    public static void SaveCheckpoint(List<TestResult> results, string filePath)
    {
        try
        {
            var json = JsonSerializer.Serialize(results, WriteOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"   [WARN] Failed to write temporary checkpoint: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>Reads previous results from the checkpoint file. Returns an empty list on failure.</summary>
    public static List<TestResult> LoadCheckpoint(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<List<TestResult>>(json, ReadOptions) ?? [];
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Failed to read checkpoint '{filePath}': {ex.Message}");
            Console.ResetColor();
            return [];
        }
    }
}