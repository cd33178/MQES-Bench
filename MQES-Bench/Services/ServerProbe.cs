using MQESBench.Models;
using OpenAI.Chat;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MQESBench.Services;

/// <summary>
/// Detects the active model and server metadata by querying standard REST endpoints.
/// Compatible with llama-server, Ollama, vLLM, LM Studio, and any OpenAI-compatible server.
/// </summary>
public static class ServerProbe
{
    /// <summary>
    /// Queries <c>/v1/models</c> and <c>/props</c> to extract model name, quantization scheme,
    /// and active context window length. All exceptions are handled gracefully to prevent
    /// blocking benchmark initialization when the server is unreachable.
    /// </summary>
    /// <param name="endpoint">The base URL of the OpenAI-compatible server (e.g., http://127.0.0.1:8080).</param>
    /// <param name="apiKey">Optional bearer authentication token.</param>
    /// <returns>A populated <see cref="ServerMetadata"/> instance, or safe defaults if unreachable.</returns>
    public static async Task<ServerMetadata> GetServerMetadataAsync(string endpoint, string? apiKey = null)
    {
        var metadata = new ServerMetadata();

        try
        {
            var uri = new Uri(endpoint);
            var baseUri = $"{uri.Scheme}://{uri.Authority}";
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(5);

            if (!string.IsNullOrWhiteSpace(apiKey) && !string.Equals(apiKey, "not-needed", StringComparison.OrdinalIgnoreCase))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            // 1. GET /v1/models — Universally supported by Ollama, vLLM, LM Studio, and llama-server
            try
            {
                var modelsJson = await http.GetFromJsonAsync<JsonElement>($"{baseUri}/v1/models");
                if (modelsJson.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                {
                    var fullPath = data[0].GetProperty("id").GetString() ?? string.Empty;
                    var fileName = Path.GetFileName(fullPath);
                    metadata.ModelFile = string.IsNullOrWhiteSpace(fileName) ? fullPath : fileName;

                    // Detect quantization scheme if embedded in the model ID or file path
                    var match = Regex.Match(
                        fullPath,
                        @"(?i)(Q\d_[A-Z0-9_]+|Q\d_K_[SML]|IQ\d_[A-Z0-9_]+|UD-Q\d_[A-Z0-9_]+|F16|F32|AWQ|GPTQ)");

                    if (match.Success)
                    {
                        metadata.Quantization = match.Value.ToUpperInvariant();
                    }
                    else if (fullPath.Contains(':'))
                    {
                        // Handle Ollama tag conventions (e.g., "qwen2.5-coder:32b" -> "32B")
                        metadata.Quantization = fullPath.Split(':').LastOrDefault()?.ToUpperInvariant() ?? "Standard";
                    }
                }
            }
            catch
            {
                // Degrade silently: the server might not implement /v1/models
            }

            // 2. GET /props — Specific to llama-server; provides the true runtime n_ctx value
            try
            {
                var propsJson = await http.GetFromJsonAsync<JsonElement>($"{baseUri}/props");
                if (propsJson.TryGetProperty("default_generation_settings", out var genSettings) &&
                    genSettings.TryGetProperty("n_ctx", out var nCtx))
                {
                    metadata.ContextSize = nCtx.GetInt32();
                }
            }
            catch
            {
                // Degrade silently: /props is optional and unique to llama-server
            }

            metadata.Hardware = $"{Environment.MachineName} ({Environment.ProcessorCount} Cores) - {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
        }
        catch
        {
            // Safe fallback: preserves empty metadata if connection fails entirely
        }

        return metadata;
    }

    /// <summary>
    /// Dynamically injects up to 4 model-appropriate stop sequences into an instance
    /// of <see cref="ChatCompletionOptions"/> based on the discovered model file name or ID.
    /// </summary>
    /// <param name="options">The target chat completion options instance.</param>
    /// <param name="modelIdentifier">The model identifier, tag, or GGUF file path.</param>
    public static void ConfigureStopSequences(ChatCompletionOptions options, string? modelIdentifier)
    {
        ArgumentNullException.ThrowIfNull(options);

        var stops = GetStopSequences(modelIdentifier);

        options.StopSequences.Clear();
        foreach (var stop in stops)
        {
            options.StopSequences.Add(stop);
        }
    }

    /// <summary>
    /// Resolves up to 4 exact stop tokens tailored to the target model family
    /// to adhere strictly to the OpenAI API limit of 4 stop sequences per request.
    /// </summary>
    /// <param name="modelIdentifier">The model file name, ID, or repo tag.</param>
    /// <returns>A read-only collection containing up to four distinct stop sequences.</returns>
    public static IReadOnlyList<string> GetStopSequences(string? modelIdentifier)
    {
        if (string.IsNullOrWhiteSpace(modelIdentifier))
        {
            return ["User:", "\nUser:", "Assistant:", "\nAssistant:"];
        }

        var name = modelIdentifier.ToLowerInvariant();

        return name switch
        {
            // DeepSeek family (requires wide fullwidth Unicode bars U+FF5C for native control tokens)
            _ when name.Contains("deepseek") =>
            [
                "<｜end of sentence｜>",
                "<｜User｜>",
                "User:",
                "Assistant:"
            ],

            // Qwen family and standard ChatML format derivatives
            _ when name.Contains("qwen") || name.Contains("chatml") =>
            [
                "<|im_end|>",
                "<|im_start|>",
                "User:",
                "Assistant:"
            ],

            // Meta Llama 3 / 3.1 / 3.2 / 3.3 architectures
            _ when name.Contains("llama-3") || name.Contains("llama3") =>
            [
                "<|eot_id|>",
                "<|end_of_text|>",
                "User:",
                "Assistant:"
            ],

            // Microsoft Phi-3 and Phi-4 dense architectures
            _ when name.Contains("phi-4") || name.Contains("phi-3") || name.Contains("phi") =>
            [
                "<|im_end|>",
                "<|endoftext|>",
                "User:",
                "Assistant:"
            ],

            // Universal turn-boundary fallback for unclassified models
            _ =>
            [
                "User:",
                "\nUser:",
                "Assistant:",
                "\nAssistant:"
            ]
        };
    }
}