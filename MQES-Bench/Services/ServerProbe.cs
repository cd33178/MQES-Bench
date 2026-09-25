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

            if (!string.IsNullOrWhiteSpace(apiKey) && !string.Equals(apiKey, "not-needed", StringComparison.OrdinalIgnoreCase))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            // 1. GET /v1/models — Discovers model file, quantization tags, and GGUF architectural metadata
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
                // Degrade silently if /v1/models is unavailable
            }

            // 2. GET /slots — Reads active runtime sampler arguments in modern llama-server builds
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
                        if (slotParams.TryGetProperty("temp", out var t) || slotParams.TryGetProperty("temperature", out t))
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
                // Degrade silently to /props fallback
            }

            // 3. GET /props — Legacy endpoint fallback for context limits and parameters
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
            // Safe fallback preserves default metadata
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

            // 1. Query /props for runtime chat_template and active stop sequences
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

            return AnalyzeTemplate(chatTemplate, stopTokens, modelIdentifier);
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
        http.Timeout = TimeSpan.FromSeconds(25);

        if (!string.IsNullOrWhiteSpace(apiKey) && !string.Equals(apiKey, "not-needed", StringComparison.OrdinalIgnoreCase))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        // Lightweight payload declaring a dummy filesystem tool
        var payload = new
        {
            messages = new[]
            {
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
            max_tokens = 64,
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
                    ErrorMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
                };
            }

            var jsonStr = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            // Extract token usage metrics
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

            // Inspect response choices
            var hasNativeTools = false;
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
                    // Check 1: Standard OpenAI structured tool_calls array
                    if (msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
                    {
                        hasNativeTools = true;
                        detectedSyntax = "Native OpenAI JSON (tool_calls array)";
                    }

                    if (msg.TryGetProperty("content", out var textContent))
                    {
                        rawContent = textContent.GetString() ?? string.Empty;
                    }
                }
            }

            // Check 2: Embedded text-based tool syntax if the array is empty
            if (!hasNativeTools && !string.IsNullOrWhiteSpace(rawContent))
            {
                if (rawContent.Contains("<tool_call>"))
                {
                    hasNativeTools = true;
                    detectedSyntax = "<tool_call>...</tool_call> (ChatML text format)";
                }
                else if (rawContent.Contains("call:write_file") || rawContent.Contains("call:"))
                {
                    hasNativeTools = true;
                    detectedSyntax = "call:<function>{...} (Gemma native format)";
                }
                else if (rawContent.Contains("[TOOL_CALLS]"))
                {
                    hasNativeTools = true;
                    detectedSyntax = "[TOOL_CALLS] (Mistral text format)";
                }
            }

            return new ModelProbeResult
            {
                ExecutedSuccessfully = true,
                HasNativeToolCalls = hasNativeTools,
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
    /// Analyzes raw Jinja templates or infers architecture, tool semantics, and agent readiness from the model identifier.
    /// </summary>
    private static ModelAgentCapabilities AnalyzeTemplate(string template, List<string> stops, string? modelIdentifier)
    {
        var resolvedStops = stops.Count > 0
            ? stops
            : [.. GetStopSequences(modelIdentifier)];

        // 1. Direct Jinja analysis if available
        if (!string.IsNullOrWhiteSpace(template))
        {
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

            var hasToolSupport = template.Contains("tools") &&
                                 (template.Contains("tool_calls") ||
                                  template.Contains("<tool_call>") ||
                                  template.Contains("<tool>") ||
                                  template.Contains("[TOOL_CALLS]") ||
                                  template.Contains("<|python_tag|>") ||
                                  template.Contains("action") ||
                                  template.Contains("call:"));

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
            else if (family == "Gemma" && hasToolSupport)
            {
                toolSyntax = "call:<function>{...} (Gemma native)";
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
                StopTokens = resolvedStops,
                Agents = EvaluateAgents(family, hasToolSupport, toolSyntax, modelIdentifier)
            };
        }

        // 2. Fallback: Architectural inference via model name / GGUF identifier
        var name = (modelIdentifier ?? string.Empty).ToLowerInvariant();

        var inferredFamily = name switch
        {
            _ when name.Contains("qwen") || name.Contains("pulsar") || name.Contains("kat-coder") || name.Contains("hermes") || name.Contains("chatml") => "ChatML",
            _ when name.Contains("llama-3") || name.Contains("llama3") => "Llama-3",
            _ when name.Contains("deepseek") => "DeepSeek",
            _ when name.Contains("phi-3") || name.Contains("phi-4") || name.Contains("phi") => "Phi-3 / Phi-4",
            _ when name.Contains("mistral") || name.Contains("codestral") => "Mistral / Llama-2",
            _ when name.Contains("gemma") => "Gemma",
            _ => "Generic / Unknown"
        };

        // Identifies whether the model is an official Instruction/Chat-tuned build
        var isInstructTuned = name.Contains("-it") ||
                              name.Contains("instruct") ||
                              name.Contains("chat") ||
                              name.Contains("hermes") ||
                              name.Contains("command-r");

        // Major frontier instruct models natively support function calling
        var isNativeToolTrained = isInstructTuned && inferredFamily is "Gemma" or "ChatML" or "Llama-3" or "DeepSeek" or "Mistral / Llama-2";

        var inferredSyntax = (isNativeToolTrained, inferredFamily) switch
        {
            (true, "Gemma") => "call:<function>{...} (Gemma native)",
            (true, "ChatML") => "<tool_call>...</tool_call> (OpenAI/Hermes/Qwen standard)",
            (true, "Llama-3") => "<|python_tag|> (Llama 3 native)",
            (true, "Mistral / Llama-2") => "[TOOL_CALLS] (Mistral native)",
            (true, "DeepSeek") => "Custom JSON Schema in body",
            _ => "None"
        };

        return new ModelAgentCapabilities
        {
            TemplateFamily = $"{inferredFamily} (Inferred from Model ID)",
            SupportsTools = isNativeToolTrained,
            ToolCallSyntax = inferredSyntax,
            StopTokens = resolvedStops,
            Agents = EvaluateAgents(inferredFamily, isNativeToolTrained, inferredSyntax, modelIdentifier)
        };
    }

    /// <summary>
    /// Evaluates compatibility across 12 distinct coding agents, autonomous frameworks, and IDE assistants.
    /// </summary>
    private static List<AgentCompatibility> EvaluateAgents(string family, bool hasVerifiedTools, string syntax, string? modelIdentifier)
    {
        var list = new List<AgentCompatibility>();
        var name = (modelIdentifier ?? string.Empty).ToLowerInvariant();

        var isInstruct = name.Contains("-it") || name.Contains("instruct") || name.Contains("chat");

        // --- Category 1: Strict Function Calling / MCP / ACI Dependent Agents ---

        // 1. OpenCode: Strict requirement for JSON schema tool calling
        var openCodeReady = hasVerifiedTools;
        list.Add(new AgentCompatibility(
            Name: "OpenCode",
            Status: openCodeReady ? "READY" : "INCOMPATIBLE",
            Details: openCodeReady ? "Tool calling schemas supported" : "No tool calling support (fails file creation)",
            IsReady: openCodeReady
        ));

        // 2. Goose (Block): Model Context Protocol & tool invocation framework
        var gooseReady = hasVerifiedTools;
        list.Add(new AgentCompatibility(
            Name: "Goose",
            Status: gooseReady ? "READY" : "INCOMPATIBLE",
            Details: gooseReady ? "Native MCP & tool execution ready" : "Cannot invoke MCP developer toolkits",
            IsReady: gooseReady
        ));

        // 3. OpenHands (OpenDevin): Autonomous runtime agent executing shell and file edits
        var openHandsReady = hasVerifiedTools;
        list.Add(new AgentCompatibility(
            Name: "OpenHands",
            Status: openHandsReady ? "READY" : "INCOMPATIBLE",
            Details: openHandsReady ? "Structured action stream supported" : "Action serialization fails without tools",
            IsReady: openHandsReady
        ));

        // 4. SWE-agent (Princeton): Autonomous agent executing bash/editor via Agent-Computer Interface
        var sweAgentReady = hasVerifiedTools;
        list.Add(new AgentCompatibility(
            Name: "SWE-agent",
            Status: sweAgentReady ? "READY" : "INCOMPATIBLE",
            Details: sweAgentReady ? "ACI command generation verified" : "Cannot execute ACI tool commands",
            IsReady: sweAgentReady
        ));

        // 5. Plandex: Multi-file development engine with sandboxed branch planning
        var plandexReady = hasVerifiedTools;
        list.Add(new AgentCompatibility(
            Name: "Plandex",
            Status: plandexReady ? "READY" : "INCOMPATIBLE",
            Details: plandexReady ? "Multi-file planning & tool execution ready" : "Fails branch plan serialization",
            IsReady: plandexReady
        ));

        // 6. Cline: Autonomous VS Code agent requiring shell and filesystem tools
        var clineReady = hasVerifiedTools;
        list.Add(new AgentCompatibility(
            Name: "Cline",
            Status: clineReady ? "READY" : "INCOMPATIBLE",
            Details: clineReady ? "Native tool calling supported" : "Cannot invoke file/system tools",
            IsReady: clineReady
        ));

        // --- Category 2: Hybrid Agents (Tool mode for autonomous actions, fallback for diffs) ---

        // 7. Cursor / Windsurf: Full agentic IDE mode vs standard inline diffs
        var cursorAgentReady = hasVerifiedTools;
        list.Add(new AgentCompatibility(
            Name: "Cursor/Windsurf",
            Status: cursorAgentReady ? "READY" : "LIMITED",
            Details: cursorAgentReady ? "Autonomous agent mode supported" : "Degrades to standard inline diff mode",
            IsReady: cursorAgentReady
        ));

        // 8. Avante.nvim: Neovim autonomous codebase assistant
        var avanteReady = hasVerifiedTools;
        list.Add(new AgentCompatibility(
            Name: "Avante.nvim",
            Status: avanteReady ? "READY" : "LIMITED",
            Details: avanteReady ? "Full codebase planning & tool support" : "Limited to direct buffer completion",
            IsReady: avanteReady
        ));

        // --- Category 3: Diff, Text, and Chat-Based Agents (Operate without Tool Calling) ---

        // 9. Aider: Native SEARCH/REPLACE git diff edits
        var aiderReady = family is "ChatML" or "Llama-3" or "DeepSeek" or "Phi-3 / Phi-4" or "Gemma" or "Mistral / Llama-2" || hasVerifiedTools;
        list.Add(new AgentCompatibility(
            Name: "Aider",
            Status: aiderReady ? "READY" : "LIMITED",
            Details: aiderReady ? "Native SEARCH/REPLACE diff mode" : "Fallback to --edit-format whole",
            IsReady: aiderReady
        ));

        // 10. Mentat: Interactive terminal assistant operating via git diffs and AST context
        var mentatReady = family is "ChatML" or "Llama-3" or "DeepSeek" or "Phi-3 / Phi-4" or "Gemma" || hasVerifiedTools;
        list.Add(new AgentCompatibility(
            Name: "Mentat",
            Status: mentatReady ? "READY" : "LIMITED",
            Details: mentatReady ? "Git diff & context parsing supported" : "May fail AST diff application",
            IsReady: mentatReady
        ));

        // 11. Continue: Context-aware IDE chat and slash commands (@workspace)
        var continueReady = !family.Contains("Raw") && !family.Contains("Unknown");
        list.Add(new AgentCompatibility(
            Name: "Continue",
            Status: continueReady ? "READY" : "BASIC",
            Details: continueReady ? "Chat and context (@workspace) ready" : "Raw autocomplete only",
            IsReady: continueReady
        ));

        // 12. Copilot CLI: Terminal shell and PowerShell command generation
        var copilotCliReady = isInstruct || family is "ChatML" or "Llama-3" or "DeepSeek" or "Gemma";
        list.Add(new AgentCompatibility(
            Name: "Copilot CLI",
            Status: copilotCliReady ? "READY" : "LIMITED",
            Details: copilotCliReady ? "Precise command generation" : "May emit conversational text",
            IsReady: copilotCliReady
        ));

        return list;
    }

    /// <summary>
    /// Dynamically injects model-appropriate stop sequences into an instance of <see cref="ChatCompletionOptions"/>.
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
    /// Resolves exact turn-boundary stop tokens tailored to the target model family.
    /// </summary>
    public static IReadOnlyList<string> GetStopSequences(string? modelIdentifier)
    {
        if (string.IsNullOrWhiteSpace(modelIdentifier))
        {
            return ["User:", "\nUser:", "Assistant:", "\nAssistant:"];
        }

        var name = modelIdentifier.ToLowerInvariant();

        return name switch
        {
            // Google Gemma architecture
            _ when name.Contains("gemma") =>
            [
                "<end_of_turn>",
                "<eos>",
                "<start_of_turn>",
                "\n<start_of_turn>"
            ],

            // DeepSeek architecture
            _ when name.Contains("deepseek") =>
            [
                "<｜end of sentence｜>",
                "<｜User｜>",
                "User:",
                "Assistant:"
            ],

            // Qwen family and standard ChatML format derivatives
            _ when name.Contains("qwen") || name.Contains("chatml") || name.Contains("pulsar") =>
            [
                "<|im_end|>",
                "<|im_start|>",
                "User:",
                "Assistant:"
            ],

            // Meta Llama 3 architectures
            _ when name.Contains("llama-3") || name.Contains("llama3") =>
            [
                "<|eot_id|>",
                "<|end_of_text|>",
                "User:",
                "Assistant:"
            ],

            // Microsoft Phi architectures
            _ when name.Contains("phi-4") || name.Contains("phi-3") || name.Contains("phi") =>
            [
                "<|im_end|>",
                "<|endoftext|>",
                "User:",
                "Assistant:"
            ],

            // Mistral / Codestral architectures
            _ when name.Contains("mistral") || name.Contains("codestral") =>
            [
                "</s>",
                "[INST]",
                "[/INST]",
                "User:"
            ],

            // Universal turn-boundary fallback
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