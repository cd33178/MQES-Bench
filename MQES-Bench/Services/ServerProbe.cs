using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MQESBench.Models;

namespace MQESBench.Services;

/// <summary>
/// Detects the active model and server metadata by querying standard REST endpoints.
/// Compatible with llama-server, Ollama, vLLM, LM Studio, and any OpenAI-compatible server.
/// </summary>
public static class ServerProbe
{
    /// <summary>
    /// Queries <c>/v1/models</c> and <c>/props</c> to extract model name, quantization,
    /// and context size. All exceptions are caught silently to avoid blocking
    /// benchmark startup if the server is unavailable.
    /// </summary>
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

            // 1. GET /v1/models — works universally on Ollama, vLLM, LM Studio, and llama-server
            try
            {
                var modelsJson = await http.GetFromJsonAsync<JsonElement>($"{baseUri}/v1/models");
                if (modelsJson.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                {
                    var fullPath = data[0].GetProperty("id").GetString() ?? "";
                    var fileName = Path.GetFileName(fullPath);
                    metadata.ModelFile = string.IsNullOrWhiteSpace(fileName) ? fullPath : fileName;

                    // Detect quantization scheme if present in the model ID or filename
                    var match = Regex.Match(fullPath, @"(?i)(Q\d_[A-Z0-9_]+|Q\d_K_[SML]|IQ\d_[A-Z0-9_]+|UD-Q\d_[A-Z0-9_]+|F16|F32|AWQ|GPTQ)");
                    if (match.Success)
                    {
                        metadata.Quantization = match.Value.ToUpper();
                    }
                    else if (fullPath.Contains(":"))
                    {
                        // Ollama tag convention (e.g. "qwen2.5-coder:32b" -> tag "32B")
                        metadata.Quantization = fullPath.Split(':').LastOrDefault()?.ToUpper() ?? "Standard";
                    }
                }
            }
            catch
            {
                // Ignored: the server may not implement /v1/models
            }

            // 2. GET /props — llama-server-specific; provides the real n_ctx of the loaded model
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
                // Ignored: /props is optional and only exists on llama-server
            }

            metadata.Hardware = $"{Environment.MachineName} ({Environment.ProcessorCount} Cores) - {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
        }
        catch
        {
            // Safe fallback: returns empty metadata if the server is unreachable
        }

        return metadata;
    }
}