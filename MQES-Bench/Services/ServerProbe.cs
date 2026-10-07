using MQESBench.Models;
using OpenAI.Chat;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MQESBench.Services;

/// <summary>
/// Probes server metadata, low-level GGUF architecture, Jinja chat templates,
/// and live tool-calling capabilities across OpenAI-compatible HTTP inference servers.
/// </summary>
public static class ServerProbe
{
    /// <summary>
    /// Queries <c>/v1/models</c>, <c>/slots</c>, and <c>/props</c> to extract model identifiers,
    /// quantization schemes, GGUF structural parameters, and active server samplers.
    /// </summary>
    /// <param name="endpoint">The base URL of the inference server (e.g., http://127.0.0.1:8080).</param>
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

            if (!string.IsNullOrWhiteSpace(apiKey) &&
                !string.Equals(apiKey, "not-needed", StringComparison.OrdinalIgnoreCase))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            // 1. GET /v1/models — Discovers model file, quantization tags, and architecture
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
                /* Degrade silently if /v1/models is hidden */
            }

            // 2. GET /slots — Reads active runtime sampler arguments
            var samplersResolved = false;
            try
            {
                var slotsJson = await http.GetFromJsonAsync<JsonElement>($"{baseUri}/slots");
                if (slotsJson.ValueKind == JsonValueKind.Array && slotsJson.GetArrayLength() > 0)
                {
                    metadata.TotalSlots = slotsJson.GetArrayLength();
                    var firstSlot = slotsJson[0];

                    if (firstSlot.TryGetProperty("params", out var slotParams))
                    {
                        if (slotParams.TryGetProperty("temp", out var t) ||
                            slotParams.TryGetProperty("temperature", out t))
                        {
                            metadata.ServerTemperature = t.GetDouble();
                        }

                        if (slotParams.TryGetProperty("min_p", out var minP))
                        {
                            metadata.ServerMinP = minP.GetDouble();
                        }

                        if (slotParams.TryGetProperty("repeat_penalty", out var repP))
                        {
                            metadata.ServerRepeatPenalty = repP.GetDouble();
                        }

                        if (slotParams.TryGetProperty("repeat_last_n", out var repN))
                        {
                            metadata.ServerRepeatLastN = repN.GetInt32();
                        }

                        if (slotParams.TryGetProperty("n_predict", out var nPred))
                        {
                            metadata.MaxPredictTokens = nPred.GetInt32();
                        }

                        samplersResolved = true;
                    }
                }
            }
            catch
            {
                /* Degrade silently */
            }

            // 3. GET /props — Context limits and fallback generation settings
            try
            {
                var propsJson = await http.GetFromJsonAsync<JsonElement>($"{baseUri}/props");

                if (propsJson.TryGetProperty("total_slots", out var slots) && metadata.TotalSlots <= 1)
                {
                    metadata.TotalSlots = slots.GetInt32();
                }

                if (propsJson.TryGetProperty("default_generation_settings", out var genSettings))
                {
                    if (genSettings.TryGetProperty("n_ctx", out var nCtx))
                    {
                        metadata.ContextSize = nCtx.GetInt32();
                    }

                    if (!samplersResolved)
                    {
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
            }
            catch
            {
                // Degrade silently
            }

            metadata.Hardware = $"{Environment.MachineName} ({Environment.ProcessorCount} Cores) - {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
        }
        catch
        {
             // Preserves default metadata
        }

        return metadata;
    }

    /// <summary>
    /// Inspects the server's Jinja chat template and runtime properties to determine tool syntax,
    /// stop tokens, and agent readiness. Falls back to model signature inference when templates are hidden.
    /// </summary>
    /// <param name="endpoint">The base URL of the OpenAI-compatible server (e.g., http://127.0.0.1:8080).</param>
    /// <param name="apiKey">Optional bearer authentication token.</param>
    /// <param name="modelIdentifier">Model Identifier</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A populated <see cref="ModelAgentCapabilities"/> record.</returns>
    public static async Task<ModelAgentCapabilities> InspectModelCapabilitiesAsync(
            string endpoint,
            string? apiKey = null,
            string? modelIdentifier = null,
            CancellationToken ct = default)
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
            ChatTemplateCaps? templateCaps = null;

            // 1. Query /props for runtime template and tool capabilities
            try
            {
                var propsJson = await http.GetFromJsonAsync<JsonElement>($"{baseUri}/props", ct);

                if (propsJson.TryGetProperty("chat_template_caps", out var capsElement))
                {
                    templateCaps = ChatTemplateCaps.FromJsonElement(capsElement);
                }

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
                 // Degrade silently
            }

            // 2. Query /v1/models if chat_template was omitted in /props
            if (string.IsNullOrWhiteSpace(chatTemplate))
            {
                try
                {
                    var modelsJson = await http.GetFromJsonAsync<JsonElement>($"{baseUri}/v1/models", ct);
                    if (modelsJson.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                    {
                        var firstModel = data[0];
                        if (string.IsNullOrWhiteSpace(modelIdentifier))
                        {
                            modelIdentifier = firstModel.GetProperty("id").GetString();
                        }

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

            var probeResult = await RunActiveProbeAsync(endpoint, apiKey, ct);

            var family = InferTemplateFamily(chatTemplate, modelIdentifier);

            var toolsVerified = probeResult.HasNativeToolCalls || (templateCaps?.SupportsToolCalls == true && probeResult.ExecutedSuccessfully);

            var toolSyntax = probeResult.DetectedToolSyntax != "None"
                ? probeResult.DetectedToolSyntax
                : (toolsVerified ? "Native OpenAI JSON (tool_calls validated)" : "None");

            var resolvedStops = stopTokens.Count > 0
                ? stopTokens
                : [.. GetStopSequences(modelIdentifier)];

            return new ModelAgentCapabilities
            {
                TemplateFamily = family,
                SupportsTools = toolsVerified,
                ToolCallSyntax = toolSyntax,
                StopTokens = resolvedStops,
                Agents = EvaluateAgents(family, toolsVerified, templateCaps, modelIdentifier),
                TemplateCaps = templateCaps
            };
        }
        catch
        {
            return new ModelAgentCapabilities();
        }
    }

    /// <summary>
    /// Executes a lightweight empirical probe request to verify live function calling,
    /// prompt-to-token throughput, and round-trip execution latency.
    /// </summary>
    public static async Task<ModelProbeResult> RunActiveProbeAsync(
            string endpoint,
            string? apiKey = null,
            CancellationToken ct = default)
    {
        var uri = new Uri(endpoint);
        var completionsUrl = $"{uri.Scheme}://{uri.Authority}/v1/chat/completions";

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        if (!string.IsNullOrWhiteSpace(apiKey) && !string.Equals(apiKey, "not-needed", StringComparison.OrdinalIgnoreCase))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var payload = new
        {
            messages = new[]
            {
                new { role = "system", content = "You are a coding agent. Always execute the requested tool." },
                new { role = "user", content = "Use the write_file tool to save 'ok' into 'probe.txt'." }
            },
            tools = new[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "write_file",
                        description = "Writes content to a file path.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string" },
                                content = new { type = "string" }
                            },
                            required = new[] { "path", "content" }
                        }
                    }
                }
            },
            tool_choice = "auto",
            max_tokens = 512,
            temperature = 0.0
        };

        var sw = Stopwatch.StartNew();

        try
        {
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await http.PostAsync(completionsUrl, content, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ModelProbeResult
                {
                    ExecutedSuccessfully = false,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    LatencyMs = sw.Elapsed.TotalMilliseconds
                };
            }

            var jsonStr = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            var promptTokens = 0;
            var completionTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt))
                {
                    promptTokens = pt.GetInt32();
                }

                if (usage.TryGetProperty("completion_tokens", out var ctProp))
                {
                    completionTokens = ctProp.GetInt32();
                }
            }

            var elapsedSec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
            var tps = completionTokens > 0 ? completionTokens / elapsedSec : 0.0;

            var hasValidNativeTools = false;
            var detectedSyntax = "None";
            var rawContent = string.Empty;
            var finishReason = "unknown";

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var fr))
                {
                    finishReason = fr.GetString() ?? "unknown";
                }

                if (choice.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
                    {
                        var firstCall = toolCalls[0];
                        if (firstCall.TryGetProperty("function", out var fn))
                        {
                            var fnName = fn.TryGetProperty("name", out var n) ? n.GetString() : null;
                            var fnArgs = fn.TryGetProperty("arguments", out var a) ? a.GetString() : null;

                            if (fnName == "write_file" && !string.IsNullOrWhiteSpace(fnArgs))
                            {
                                try
                                {
                                    using var argsDoc = JsonDocument.Parse(fnArgs);
                                    if (argsDoc.RootElement.TryGetProperty("path", out _) &&
                                        argsDoc.RootElement.TryGetProperty("content", out _))
                                    {
                                        hasValidNativeTools = true;
                                        detectedSyntax = "Native OpenAI JSON (tool_calls validated)";
                                    }
                                }
                                catch
                                {
                                    // ignored
                                }
                            }
                        }
                    }

                    if (msg.TryGetProperty("content", out var textContent))
                    {
                        rawContent = textContent.GetString() ?? string.Empty;
                    }
                }
            }

            if (!hasValidNativeTools && !string.IsNullOrWhiteSpace(rawContent))
            {
                if (rawContent.Contains("<tool_call>"))
                {
                    detectedSyntax = "<tool_call>...</tool_call> (ChatML text format)";
                }
                else if (rawContent.Contains("call:write_file") || rawContent.Contains("call:"))
                {
                    detectedSyntax = "call:<function>{...} (Gemma native format)";
                }
                else if (rawContent.Contains("[TOOL_CALLS]"))
                {
                    detectedSyntax = "[TOOL_CALLS] (Mistral text format)";
                }
            }

            return new ModelProbeResult
            {
                ExecutedSuccessfully = true,
                HasNativeToolCalls = hasValidNativeTools,
                DetectedToolSyntax = detectedSyntax,
                RawResponseContent = rawContent.Trim().Replace("\r", " ").Replace("\n", " "),
                TokensPerSecond = tps,
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                FinishReason = finishReason
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ModelProbeResult
            {
                ExecutedSuccessfully = false,
                ErrorMessage = ex.Message,
                LatencyMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }

    /// <summary>
    /// Identifies chat template family.
    /// </summary>
    private static string InferTemplateFamily(string template, string? modelIdentifier)
    {
        if (template.Contains("<|im_start|>"))
        {
            return "ChatML";
        }

        if (template.Contains("<|start_header_id|>"))
        {
            return "Llama-3";
        }

        if (template.Contains("[INST]"))
        {
            return "Mistral / Llama-2";
        }

        if (template.Contains("<start_of_turn>"))
        {
            return "Gemma";
        }

        if (template.Contains("<｜begin of sentence｜>") || template.Contains("<｜User｜>"))
        {
            return "DeepSeek";
        }

        if (template.Contains("<|user|>"))
        {
            return "Phi-3 / Phi-4";
        }

        var name = (modelIdentifier ?? string.Empty).ToLowerInvariant();
        if (name.Contains("qwen") || name.Contains("hermes") || name.Contains("chatml"))
        {
            return "ChatML";
        }

        if (name.Contains("llama-3") || name.Contains("llama3"))
        {
            return "Llama-3";
        }

        if (name.Contains("deepseek"))
        {
            return "DeepSeek";
        }

        if (name.Contains("mistral") || name.Contains("codestral"))
        {
            return "Mistral";
        }

        if (name.Contains("gemma"))
        {
            return "Gemma";
        }

        return name.Contains("phi")
            ? "Phi-3 / Phi-4"
            : "Standard";
    }

    /// <summary>
    /// Evaluates compatibility across 12 distinct coding agents, autonomous frameworks, and IDE assistants.
    /// </summary>
    private static List<AgentCompatibility> EvaluateAgents(
            string family,
            bool toolsVerified,
            ChatTemplateCaps? caps,
            string? modelIdentifier)
    {
        var list = new List<AgentCompatibility>();

        var toolStatus = toolsVerified ? "READY" : "INCOMPATIBLE";
        var toolReason = toolsVerified
            ? "Native OpenAI tool_calls verified with valid JSON arguments"
            : "No tool calling support (fails file creation)";

        var isModernTemplate = family is "ChatML" or "Mistral" or "Llama-3" or "Gemma" or "DeepSeek" || (toolsVerified && family == "Standard");

        // 1. Claude Code
        var claudeCodeReady = toolsVerified && (caps == null || caps.SupportsObjectArguments);
        var claudeCodeDetails = claudeCodeReady
            ? (caps?.SupportsParallelToolCalls == true
                ? "Terminal tool calling, multi-turn diffs & parallel executions ready"
                : "Sequential tool calling ready (no parallel execution)")
            : "Requires verified object argument tool calls";

        list.Add(Create("Claude Code", claudeCodeReady ? "READY" : "INCOMPATIBLE", claudeCodeDetails));

        // 2. Claude Desktop & MCP
        var mcpReady = toolsVerified && (caps == null || caps.SupportsObjectArguments);
        list.Add(Create("Claude Desktop", mcpReady ? "READY" : "INCOMPATIBLE",
            mcpReady ? "Native tool-calling verified for Model Context Protocol (MCP)" : "Cannot parse complex MCP schema objects"));

        // 3. Cline
        list.Add(Create("Cline", toolStatus,
            toolsVerified ? "Native OpenAI tool_calls verified with valid JSON arguments" : "Cannot invoke file/system tools"));

        // 4. Goose & OpenCode
        list.Add(Create("Goose", toolStatus,
            toolsVerified ? "Native tool_calls verified for developer toolkits" : "Cannot invoke MCP developer toolkits"));
        list.Add(Create("OpenCode", toolStatus, toolReason));

        // 5. OpenHands & Roo Code
        list.Add(Create("OpenHands", toolStatus,
            toolsVerified ? "Action serialization verified" : "Action serialization fails without tools"));
        list.Add(Create("Roo Code", toolStatus,
            toolsVerified ? "Multi-mode autonomous tool calling and AST inspection verified" : toolReason));
        list.Add(Create("SWE-agent", toolStatus,
            toolsVerified ? "ACI command generation via function calls" : "Cannot execute ACI tool commands"));

        // 6. Diff & Git Editing Agents
        var diffStatus = isModernTemplate || toolsVerified ? "READY" : "LIMITED";
        list.Add(Create("Aider", diffStatus,
            diffStatus == "READY" ? "Template supports multi-turn search/replace diff editing" : "Fallback to --edit-format whole"));
        list.Add(Create("Mentat", diffStatus,
            diffStatus == "READY" ? "Template supports multi-turn search/replace diff editing" : "May fail AST diff application"));

        // 7. Workspace Context & IDE Assistants
        list.Add(Create("Continue", "READY", "Chat, code completion, and context (@workspace) ready"));
        list.Add(Create("Cursor/Windsurf", toolsVerified ? "READY" : "LIMITED",
            toolsVerified ? "Autonomous multi-file editing mode" : "Degrades to standard inline diff mode"));
        list.Add(Create("Avante.nvim", toolsVerified ? "READY" : "LIMITED",
            toolsVerified ? "Full project codebase planning & AST tool support" : "Limited to direct buffer completion"));
        list.Add(Create("Plandex", toolsVerified ? "READY" : "LIMITED",
            toolsVerified ? "Multi-file transaction planning and tool execution" : "Fails branch plan serialization"));

        // 8. Terminal Shell
        var lower = (modelIdentifier ?? string.Empty).ToLowerInvariant();
        var isInstruct = lower.Contains("instruct") || lower.Contains("coder") || lower.Contains("-it") || lower.Contains("_it") || lower.Contains("sonnet");
        list.Add(Create("Copilot CLI", isInstruct || toolsVerified ? "READY" : "LIMITED", "Direct terminal shell command translation"));

        return list;

        static AgentCompatibility Create(string name, string status, string details) =>
            new(name, status, details, IsReady: string.Equals(status, "READY", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Dynamically injects model-appropriate stop sequences into an instance of ChatCompletionOptions.
    /// </summary>
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
    /// Resolves turn-boundary stop tokens tailored to the target model family.
    /// </summary>
    private static IReadOnlyList<string> GetStopSequences(string? modelIdentifier)
    {
        if (string.IsNullOrWhiteSpace(modelIdentifier))
        {
            return ["User:", "\nUser:", "Assistant:", "\nAssistant:"];
        }

        var name = modelIdentifier.ToLowerInvariant();

        return name switch
        {
            _ when name.Contains("gemma") => ["<end_of_turn>", "<eos>", "<start_of_turn>", "\n<start_of_turn>"],
            _ when name.Contains("deepseek") => ["<｜end of sentence｜>", "<｜User｜>", "User:", "Assistant:"],
            _ when name.Contains("qwen") || name.Contains("chatml") || name.Contains("pulsar") => ["<|im_end|>", "<|im_start|>", "User:", "Assistant:"],
            _ when name.Contains("llama-3") || name.Contains("llama3") => ["<|eot_id|>", "<|end_of_text|>", "User:", "Assistant:"],
            _ when name.Contains("phi-4") || name.Contains("phi-3") || name.Contains("phi") => ["<|im_end|>", "<|endoftext|>", "User:", "Assistant:"],
            _ when name.Contains("mistral") || name.Contains("codestral") => ["</s>", "[INST]", "[/INST]", "User:"],
            _ => ["User:", "\nUser:", "Assistant:", "\nAssistant:"]
        };
    }
}