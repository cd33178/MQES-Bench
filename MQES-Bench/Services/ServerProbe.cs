using MQESBench.Models;
using OpenAI.Chat;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MQESBench.Services;

/// <summary>
/// Detects the active model, server metadata, and agent/tool capabilities by querying standard REST endpoints.
/// Compatible with llama-server, Ollama, vLLM, LM Studio, and any OpenAI-compatible server.
/// Inspects active model metadata, Jinja chat templates, and agent compatibility
/// by querying standard REST endpoints (llama-server, Ollama, vLLM, LM Studio).
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

            // 1. GET /v1/models — Extracts file, quant, and deep GGUF architectural meta
            try
            {
                var modelsJson = await http.GetFromJsonAsync<JsonElement>($"{baseUri}/v1/models");
                if (modelsJson.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                {
                    var firstModel = data[0];
                    var fullPath = firstModel.GetProperty("id").GetString() ?? string.Empty;
                    var fileName = Path.GetFileName(fullPath);
                    metadata.ModelFile = string.IsNullOrWhiteSpace(fileName) ? fullPath : fileName;

                    var match = Regex.Match(
                        fullPath,
                        @"(?i)(Q\d_[A-Z0-9_]+|Q\d_K_[SML]|IQ\d_[A-Z0-9_]+|UD-Q\d_[A-Z0-9_]+|F16|F32|AWQ|GPTQ)");

                    if (match.Success)
                    {
                        metadata.Quantization = match.Value.ToUpperInvariant();
                    }
                    else if (fullPath.Contains(':'))
                    {
                        metadata.Quantization = fullPath.Split(':').LastOrDefault()?.ToUpperInvariant() ?? "Standard";
                    }

                    // Extract deep architecture specs if exposed under 'meta'
                    if (firstModel.TryGetProperty("meta", out var meta))
                    {
                        if (meta.TryGetProperty("n_ctx_train", out var nCtxTrain))
                        {
                            metadata.TrainingContextSize = nCtxTrain.GetInt32();
                        }

                        if (meta.TryGetProperty("n_vocab", out var nVocab))
                        {
                            metadata.VocabularySize = nVocab.GetInt32();
                        }

                        if (meta.TryGetProperty("n_layer", out var nLayer))
                        {
                            metadata.LayerCount = nLayer.GetInt32();
                        }

                        if (meta.TryGetProperty("n_embd", out var nEmbd))
                        {
                            metadata.EmbeddingDimension = nEmbd.GetInt32();
                        }

                        if (meta.TryGetProperty("n_head", out var nHead))
                        {
                            metadata.AttentionHeads = nHead.GetInt32();
                        }

                        if (meta.TryGetProperty("n_head_kv", out var nHeadKv))
                        {
                            metadata.KeyValueHeads = nHeadKv.GetInt32();
                        }
                    }
                }
            }
            catch
            {
                // Degrade silently
            }

            // 2. GET /props — Specific to llama-server; extracts runtime slots, n_ctx, and server-side samplers
            try
            {
                var propsJson = await http.GetFromJsonAsync<JsonElement>($"{baseUri}/props");

                if (propsJson.TryGetProperty("total_slots", out var slots))
                {
                    metadata.TotalSlots = slots.GetInt32();
                }

                if (propsJson.TryGetProperty("default_generation_settings", out var genSettings))
                {
                    if (genSettings.TryGetProperty("n_ctx", out var nCtx))
                    {
                        metadata.ContextSize = nCtx.GetInt32();
                    }

                    if (genSettings.TryGetProperty("n_predict", out var nPred))
                    {
                        metadata.MaxPredictTokens = nPred.GetInt32();
                    }

                    if (genSettings.TryGetProperty("temperature", out var temp))
                    {
                        metadata.ServerTemperature = temp.GetDouble();
                    }

                    if (genSettings.TryGetProperty("min_p", out var minP))
                    {
                        metadata.ServerMinP = minP.GetDouble();
                    }

                    if (genSettings.TryGetProperty("repeat_penalty", out var repPen))
                    {
                        metadata.ServerRepeatPenalty = repPen.GetDouble();
                    }

                    if (genSettings.TryGetProperty("repeat_last_n", out var repLast))
                    {
                        metadata.ServerRepeatLastN = repLast.GetInt32();
                    }
                }
            }
            catch
            {
                // Degrade silently
            }

            metadata.Hardware = $"{Environment.MachineName} ({Environment.ProcessorCount} Cores) - {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
        }
        catch
        {
            // Fallback preserves safe defaults
        }

        return metadata;
    }

    /// <summary>
    /// Inspects the server's Jinja chat template and runtime generation properties
    /// to determine tool syntax, stop tokens, and multi-agent compatibility.
    /// </summary>
    /// <param name="endpoint">The base URL of the OpenAI-compatible server (e.g., http://127.0.0.1:8080).</param>
    /// <param name="apiKey">Optional bearer authentication token.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A populated <see cref="ModelAgentCapabilities"/> record.</returns>
    public static async Task<ModelAgentCapabilities> InspectModelCapabilitiesAsync(string endpoint, string? apiKey = null, CancellationToken ct = default)
    {
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

            var chatTemplate = string.Empty;
            var stopTokens = new List<string>();

            // 1. Query /props (primary source for chat_template and active stop tokens in llama-server)
            try
            {
                var propsJson = await http.GetFromJsonAsync<JsonElement>($"{baseUri}/props", ct);
                if (propsJson.TryGetProperty("default_generation_settings", out var genSettings))
                {
                    if (genSettings.TryGetProperty("chat_template", out var tmpl))
                    {
                        chatTemplate = tmpl.GetString() ?? string.Empty;
                    }

                    if (genSettings.TryGetProperty("stop", out var stops) && stops.ValueKind == JsonValueKind.Array)
                    {
                        stopTokens = stops.EnumerateArray()
                            .Select(s => s.GetString() ?? string.Empty)
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
                    }
                }
            }
            catch
            {
                // Degrade silently to fallback
            }

            // 2. Fallback: Query /v1/models if chat_template was absent in /props
            if (string.IsNullOrWhiteSpace(chatTemplate))
            {
                try
                {
                    var modelsJson = await http.GetFromJsonAsync<JsonElement>($"{baseUri}/v1/models", ct);
                    if (modelsJson.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                    {
                        var firstModel = data[0];
                        if (firstModel.TryGetProperty("meta", out var meta) &&
                            meta.TryGetProperty("chat_template", out var metaTmpl))
                        {
                            chatTemplate = metaTmpl.GetString() ?? string.Empty;
                        }
                    }
                }
                catch
                {
                    // Degrade silently
                }
            }

            return AnalyzeTemplate(chatTemplate, stopTokens);
        }
        catch
        {
            return new ModelAgentCapabilities();
        }
    }

    /// <summary>
    /// Analyzes a raw Jinja chat template string to detect template families, tool calling syntax, and agent readiness.
    /// </summary>
    private static ModelAgentCapabilities AnalyzeTemplate(string template, List<string> stops)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return new ModelAgentCapabilities
            {
                TemplateFamily = "Raw / Non-Jinja",
                StopTokens = stops,
                Agents = EvaluateAgents("Raw / Non-Jinja", false, "None")
            };
        }

        // 1. Detect Chat Template Architecture Family
        var family = template switch
        {
            _ when template.Contains("<|im_start|>") => "ChatML",
            _ when template.Contains("<|start_header_id|>") => "Llama-3",
            _ when template.Contains("[INST]") => "Mistral / Llama-2",
            _ when template.Contains("<｜begin of sentence｜>") || template.Contains("<｜User｜>") => "DeepSeek",
            _ when template.Contains("<start_of_turn>") => "Gemma",
            _ when template.Contains("<|user|>") => "Phi-3 / Phi-4",
            _ => "Generic Jinja"
        };

        // 2. Identify Function / Tool Calling Support in Jinja logic
        var hasToolSupport = template.Contains("tools") &&
                             (template.Contains("tool_calls") ||
                              template.Contains("<tool_call>") ||
                              template.Contains("<tool>") ||
                              template.Contains("[TOOL_CALLS]") ||
                              template.Contains("<|python_tag|>") ||
                              template.Contains("action"));

        // 3. Classify exact syntax used for emitting tool calls
        var toolSyntax = "None";
        if (template.Contains("<tool_call>"))
        {
            toolSyntax = "<tool_call>...</tool_call> (OpenAI/Hermes standard)";
        }
        else if (template.Contains("<tool>"))
        {
            toolSyntax = "<tool>...</tool> (Custom variant)";
        }
        else if (template.Contains("<|python_tag|>"))
        {
            toolSyntax = "<|python_tag|> (Llama 3 native)";
        }
        else if (template.Contains("[TOOL_CALLS]"))
        {
            toolSyntax = "[TOOL_CALLS] (Mistral native)";
        }
        else if (hasToolSupport)
        {
            toolSyntax = "Custom JSON Schema in body";
        }

        return new ModelAgentCapabilities
        {
            TemplateFamily = family,
            SupportsTools = hasToolSupport,
            ToolCallSyntax = toolSyntax,
            StopTokens = stops,
            Agents = EvaluateAgents(family, hasToolSupport, toolSyntax)
        };
    }

    /// <summary>
    /// Extensible evaluation matrix for coding agents and development assistants.
    /// Add future agents here without altering presentation or logging layers.
    /// </summary>
    private static List<AgentCompatibility> EvaluateAgents(string family, bool hasTools, string syntax)
    {
        var list = new List<AgentCompatibility>();

        // 1. OpenCode (CLI agent specialized in tool_calls and multi-file code editing)
        var openCodeReady = hasTools && (syntax.Contains("<tool_call>") || family == "ChatML");
        list.Add(new AgentCompatibility(
            Name: "OpenCode",
            Status: openCodeReady ? "READY" : "RISK",
            Details: openCodeReady ? "Standard <tool_call> schema" : "May fail tool parser regex",
            IsReady: openCodeReady
        ));

        // 2. Aider (Repository editing agent operating via git diffs and whole-file rewrites)
        var aiderReady = family is "ChatML" or "Llama-3" or "DeepSeek" or "Phi-3 / Phi-4" || hasTools;
        list.Add(new AgentCompatibility(
            Name: "Aider",
            Status: aiderReady ? "READY" : "LIMITED",
            Details: aiderReady ? "Native diff/whole & template" : "Fallback to --edit-format whole",
            IsReady: aiderReady
        ));

        // 3. Continue (VS Code / Visual Studio extension for codebase context, FIM, and slash commands)
        var continueReady = family != "Raw / Non-Jinja";
        list.Add(new AgentCompatibility(
            Name: "Continue",
            Status: continueReady ? "READY" : "BASIC",
            Details: continueReady ? "Full context & slash commands" : "Raw autocomplete only",
            IsReady: continueReady
        ));

        // 4. Cline / Roo Code (Autonomous coding agent executing commands, file diffs, and test suites)
        var clineReady = hasTools;
        var clineLimited = !hasTools && family is "ChatML" or "Llama-3";
        list.Add(new AgentCompatibility(
            Name: "Cline",
            Status: clineReady ? "READY" : clineLimited ? "LIMITED" : "INCOMPATIBLE",
            Details: clineReady ? "Native function calling" : clineLimited ? "Requires XML tool fallback" : "No tool support",
            IsReady: clineReady
        ));

        // 5. Copilot CLI (Terminal assistant focused on concise shell and PowerShell command generation)
        var copilotCliReady = family is "ChatML" or "Llama-3" or "DeepSeek";
        list.Add(new AgentCompatibility(
            Name: "Copilot CLI",
            Status: copilotCliReady ? "READY" : "LIMITED",
            Details: copilotCliReady ? "Precise command generation" : "May emit conversational noise",
            IsReady: copilotCliReady
        ));

        return list;
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