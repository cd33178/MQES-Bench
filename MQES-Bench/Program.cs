using MQESBench;
using MQESBench.Models;
using MQESBench.Reporting;
using MQESBench.Scoring;
using MQESBench.Services;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Runtime;
using System.Text;

// ============================================================================
// 1. CLI Parameter Parsing & Options Configuration
// ============================================================================
const string checkpointFilePath = "benchmark_checkpoint.json";

var endpoint = "http://localhost:8080/v1";
string? cliGeneratorModel = null;
var generatorApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY") ?? "not-needed"; // -ak or --api-key

string? judgeEndpoint = null;                                                    // -je or --judge-endpoint: dedicated judge URL (e.g., Ollama)
string? cliJudgeModel = null;                                                    // -jm or --judge-model: model name for judge
var cliJudgeApiKey = Environment.GetEnvironmentVariable("JUDGE_API_KEY"); // -jk or --judge-key: optional API key for judge

string? categoryFilterRaw = null;
string? categoryContainsFilterRaw = null;
string? testRangeFilterRaw = null;
var jsonFilePath = "benchmark_suite.json";
var listOnly = false;
var noJudge = false;
var resumeMode = false;
var costPerKwh = 0.15;
var requestTimeout = TimeSpan.FromMinutes(30);                        // -t or --timeout: Candidate generation timeout (default: 30m)
TimeSpan? cliJudgeTimeout = null;                                             // -jt or --judge-timeout: Dedicated judge timeout override
var completedAll = false;
var judgeMaxTokens = 1024;

int? maxOutputTokens = null;
float? cliTemperature = null;
float? cliTopP = null;

var inspectOnly = false;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i].ToLowerInvariant();

    switch (arg)
    {
        // Candidate Generator Model Name (e.g., "qwen2.5-coder:32b", "deepseek-coder", "unsloth/Qwen3-Coder-30B")
        case "--model" or "-m" or "--generator-model" or "-gm" when i + 1 < args.Length:
            cliGeneratorModel = args[++i];
            break;        // Candidate Generator API Key
        case "--api-key" or "-ak" or "--key" or "-gk" or "--generator-key" when i + 1 < args.Length:
            generatorApiKey = args[++i];
            break;
        // Dedicated Judge Endpoint (e.g., http://localhost:11434/v1 for Ollama)
        case "--judge-endpoint" or "-je" when i + 1 < args.Length:
            judgeEndpoint = args[++i];
            break;
        // Dedicated Judge Model Name (e.g., "qwen2.5-coder:32b", "llama3.1:70b")
        case "--judge-model" or "-jm" when i + 1 < args.Length:
            cliJudgeModel = args[++i];
            break;
        // Optional API key for the judge
        case "--judge-key" or "-jk" or "--judge-api-key" when i + 1 < args.Length:
            cliJudgeApiKey = args[++i];
            break;
        case "--judge-timeout" or "-jt" when i + 1 < args.Length:
            if (int.TryParse(args[++i], out var judgeSecs))
            {
                cliJudgeTimeout = TimeSpan.FromSeconds(judgeSecs);
            }
            break;
        case "--category" or "-c" when i + 1 < args.Length:
            categoryFilterRaw = args[++i];
            break;
        case "--category-contains" or "-cc" or "--cat" when i + 1 < args.Length:
            categoryContainsFilterRaw = args[++i];
            break;
        case "--cost-kwh" or "-ck" when i + 1 < args.Length:
            if (double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out var parsedCost))
            {
                costPerKwh = parsedCost;
            }
            break;
        case "--endpoint" or "-e" when i + 1 < args.Length:
            endpoint = args[++i];
            break;
        case "--file" or "-f" or "--json" or "-j" when i + 1 < args.Length:
            jsonFilePath = args[++i];
            break;
        case "--list" or "-l":
            listOnly = true;
            break;
        case "--no-judge" or "-nj":
            noJudge = true;
            break;
        case "--numbers" or "-n" when i + 1 < args.Length:
            testRangeFilterRaw = args[++i];
            break;
        case "--resume" or "-r":
            resumeMode = true;
            break;
        case "--timeout" or "-to" or "-t" when i + 1 < args.Length:
            if (int.TryParse(args[++i], out var secs))
            {
                requestTimeout = TimeSpan.FromSeconds(secs);
            }
            break;
        case "--tokens" or "-k" or "--max-tokens" or "-mt" when i + 1 < args.Length:
            if (int.TryParse(args[++i], out var tokens) && tokens > 0)
            {
                maxOutputTokens = tokens;
            }
            break;
        case "--temp" or "--temperature" when i + 1 < args.Length:
            if (float.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out var temp))
            {
                cliTemperature = temp;
            }
            break;
        case "--top-p" or "-p" when i + 1 < args.Length:
            if (float.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out var topP))
            {
                cliTopP = topP;
            }
            break;
        case "-jtk" or "--judge-tokens":
            if (i + 1 < args.Length && int.TryParse(args[++i], out var judgeTokens) && judgeTokens > 0)
            {
                judgeMaxTokens = judgeTokens;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ERROR] Missing or invalid integer value for --judge-tokens.");
                Console.ResetColor();
                return;
            }
            break;
        case "-i":
        case "--info":
        case "--inspect":
            inspectOnly = true;
            break;
        case "--help" or "-h" or "-?":
            ConsoleReporter.ShowHelp();
            return;
        default:
            if (!arg.StartsWith('-'))
            {
                var positional = args[i];
                if (positional.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || File.Exists(positional))
                {
                    jsonFilePath = positional;
                }
                else if (positional.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || positional.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    endpoint = positional;
                }
                else
                {
                    jsonFilePath = positional;
                }
            }
            break;
    }
}

// Effective Judge URL fallback: if not supplied, use candidate generator endpoint
var effectiveJudgeEndpoint = !string.IsNullOrWhiteSpace(judgeEndpoint) ? judgeEndpoint : endpoint;
var effectiveJudgeApiKey = !string.IsNullOrWhiteSpace(cliJudgeApiKey) ? cliJudgeApiKey : generatorApiKey;
var effectiveJudgeTimeout = cliJudgeTimeout ?? requestTimeout;

// ============================================================================
// 2. Hardware Profiling & Parameter Auto-Tuning
// ============================================================================

var serverMetadata = await ServerProbe.GetServerMetadataAsync(endpoint);
var capabilities = await ServerProbe.InspectModelCapabilitiesAsync(endpoint);
if (inspectOnly)
{
    var meta = await ServerProbe.GetServerMetadataAsync(endpoint, generatorApiKey);
    var caps = await ServerProbe.InspectModelCapabilitiesAsync(endpoint, generatorApiKey);

    ConsoleReporter.PrintModelInspectionReport(endpoint, meta, caps);
    return;
}

var profile = SystemTelemetry.CurrentProfile;

// Resolve Effective Generator Model Identifier: CLI override -> ServerProbe probed ID -> default fallback
var effectiveGeneratorModel = !string.IsNullOrWhiteSpace(cliGeneratorModel)
    ? cliGeneratorModel
    : !string.IsNullOrWhiteSpace(serverMetadata.ModelFile) && serverMetadata.ModelFile != "llama-server"
        ? serverMetadata.ModelFile
        : "default-model";

// Ensure metadata reflects the active model name if overridden via CLI
if (!string.IsNullOrWhiteSpace(cliGeneratorModel))
{
    serverMetadata.ModelFile = cliGeneratorModel;
}

// Resolve Effective Judge Model (Inherits generator model in Self-Judge mode)
var effectiveJudgeModel = !string.IsNullOrWhiteSpace(cliJudgeModel)
    ? cliJudgeModel
    : effectiveGeneratorModel;

var tuned = InferenceConfigurator.ResolveParameters(
    serverMetadata,
    profile,
    cliMaxTokens: maxOutputTokens,
    cliTemperature: cliTemperature,
    cliTopP: cliTopP
);

var activeMaxOutputTokens = tuned.MaxOutputTokens;

// ============================================================================
// 3. Load Benchmark Test Suite
// ============================================================================
var suiteContainer = SuiteManager.GetBenchmarkSuite(jsonFilePath);
var allTests = suiteContainer.Tests;

if (allTests.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[ERROR] No test cases available in '{jsonFilePath}'.");
    Console.ResetColor();
    return;
}

if (listOnly)
{
    ConsoleReporter.ListCategories(allTests);
    return;
}

// Explicit types provide the target-type for collection expressions []
var exactCategories = !string.IsNullOrWhiteSpace(categoryFilterRaw) && !categoryFilterRaw.Equals("ALL", StringComparison.OrdinalIgnoreCase)
    ? categoryFilterRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
    : [];

var containsCategories = !string.IsNullOrWhiteSpace(categoryContainsFilterRaw) && !categoryContainsFilterRaw.Equals("ALL", StringComparison.OrdinalIgnoreCase)
    ? categoryContainsFilterRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
    : [];

var isAll = exactCategories.Count == 0 && containsCategories.Count == 0;

var selectedTests = isAll
    ? allTests
    : allTests.Where(t =>
        (exactCategories.Count > 0 && exactCategories.Any(cat => t.Category.Equals(cat, StringComparison.OrdinalIgnoreCase))) ||
        (containsCategories.Count > 0 && containsCategories.Any(cat =>
            t.Category.Contains(cat, StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains(cat, StringComparison.OrdinalIgnoreCase)))
    ).ToList();

if (!string.IsNullOrWhiteSpace(testRangeFilterRaw))
{
    var targetIndices = SuiteManager.ParseIndexRanges(testRangeFilterRaw, selectedTests.Count);
    selectedTests = selectedTests.Where((_, index) => targetIndices.Contains(index + 1)).ToList();
}

if (selectedTests.Count == 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("[ERROR] No tests matched the specified filters.");
    Console.ResetColor();
    return;
}

var (totalRamGb, usedRamGb, _, ramLoadPct) = SystemTelemetry.GetSystemMemoryInfo();
var suiteTitle = string.IsNullOrWhiteSpace(suiteContainer.Name) ? Path.GetFileName(jsonFilePath) : suiteContainer.Name;
var isDedicatedJudge = !string.Equals(endpoint, effectiveJudgeEndpoint, StringComparison.OrdinalIgnoreCase);

// Format all registered agents dynamically with a 16-character left column alignment
var formattedAgents = string.Join(Environment.NewLine, capabilities.Agents.Select(a =>
    $"  {a.Name} Agent".PadRight(16) + $": {a.Status,-7} ({a.Details})"));

Console.WriteLine($"""
================================================================================
  LLama-Server HTTP Evaluator (.NET 10 / C# 14)
================================================================================
  Host System   : {Environment.MachineName} ({profile.PlatformType})
  CPU Model     : {profile.CpuName} ({profile.LogicalCores} Logical Cores)
  RAM Topology  : {totalRamGb:F1} GB Total (~{profile.EstimatedDimms} DIMMs @ Max {profile.RamMaxWatts:F0}W) - {ramLoadPct}% loaded
  System RAM    : {usedRamGb:F1} GB in use / {totalRamGb:F1} GB Total ({ramLoadPct}% loaded)
  CLR Runtime   : .NET {Environment.Version} (GC: {(GCSettings.IsServerGC ? "Server GC" : "Workstation GC")})
--------------------------------------------------------------------------------
  Suite File    : {Path.GetFileName(jsonFilePath)} ({suiteTitle})
  LLM Endpoint  : {endpoint}
  Model         : {serverMetadata.ModelFile} ({serverMetadata.Quantization})
  Template/Jinja: {capabilities.TemplateFamily} (Tools: {(capabilities.SupportsTools ? "Supported" : "None")})
  Tool Syntax   : {capabilities.ToolCallSyntax}
{formattedAgents}
  Stop Tokens   : {(capabilities.StopTokens.Count > 0 ? string.Join(", ", capabilities.StopTokens) : "Default fallback")}
  Judge Config  : {effectiveJudgeEndpoint} [Model: {effectiveJudgeModel}]{(isDedicatedJudge ? " (External Judge)" : " (Self-Judge)")}
  Capacity Fact : {profile.HardwareCapacityFactor:F2}x baseline multiplier
  Power Profile : TDP Max: {profile.CpuMaxWatts:F0}W | Mult: {profile.InstructionMultiplier:F2}x | PSU: {profile.PsuEfficiency * 100:F0}%
  Context Size  : {serverMetadata.ContextSize:N0} tokens | Max Output: {activeMaxOutputTokens:N0} tokens
  Sampler Auto  : Temp: {tuned.Temperature:F2} | TopP: {tuned.TopP:F2} | Strategy: {tuned.ProfileDescription}
  Power Rate    : ${costPerKwh:F2} USD / kWh
  Mode          : {(noJudge ? "Throughput Only (--no-judge)" : "Full Criteria Evaluation (MQES Enabled)")}
  Started At    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
================================================================================
""");

// ============================================================================
// 4. OpenAI Client & Cancellation Setup
// ============================================================================
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (!cts.IsCancellationRequested)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n\n[INTERRUPT DETECTED] Cancelling active request and saving state...");
        Console.ResetColor();
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }
};

// 1. Primary Client for Generation (Under Test - works with Ollama, vLLM, llama-server, Unsloth, etc.)
var generatorClientOptions = new OpenAIClientOptions
{
    Endpoint = new Uri(endpoint),
    NetworkTimeout = requestTimeout,
    RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
};

var chatClient = new ChatClient(
    model: effectiveGeneratorModel,
    credential: new ApiKeyCredential(generatorApiKey),
    options: generatorClientOptions
);

// 2. Dedicated Judge Client
var judgeClientOptions = new OpenAIClientOptions
{
    Endpoint = new Uri(effectiveJudgeEndpoint),
    NetworkTimeout = effectiveJudgeTimeout,
    RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
};

var judgeChatClient = new ChatClient(
    model: effectiveJudgeModel,
    credential: new ApiKeyCredential(effectiveJudgeApiKey),
    options: judgeClientOptions
);

List<TestResult> results = [];
var number = 1;
var runSw = Stopwatch.StartNew();

if (resumeMode && File.Exists(checkpointFilePath))
{
    results = CheckpointManager.LoadCheckpoint(checkpointFilePath);
    var completedNames = results.Select(r => r.Test.Name).ToHashSet();
    selectedTests = selectedTests.Where(t => !completedNames.Contains(t.Name)).ToList();

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[RESUME] Loaded {results.Count} previous test results. {selectedTests.Count} tests remaining.\n");
    Console.ResetColor();
}

// ============================================================================
// 5. Benchmark Execution Loop
// ============================================================================
try
{
    foreach (var test in selectedTests)
    {
        cts.Token.ThrowIfCancellationRequested();
        var snapshot = SystemTelemetry.TakeSnapshot();

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] --> Running test {number} of {selectedTests.Count}: [{test.Category}] {test.Name}...");

        var effectiveSystemPrompt = !string.IsNullOrWhiteSpace(test.SystemPrompt)
            ? test.SystemPrompt
            : !string.IsNullOrWhiteSpace(suiteContainer.DefaultSystemPrompt)
                ? suiteContainer.DefaultSystemPrompt
                : """
                  You are a Senior .NET Application Architect and SQL Server DBA expert in C# (.NET 8/9/10), CLR runtime internals (CLR, IL, GC, Memory Management), and T-SQL.
                  When answering theoretical, design, and code refactoring challenges:
                  1. Be extremely precise with CLR runtime internals and exact database terminology (e.g., state machine heap allocations, lock types, memory layout).
                  2. Provide complete, production-ready, fully compiling code solutions without omitting key logic.
                  3. Structure responses concisely while covering deep architectural mechanics.

                  CRITICAL: Keep your internal reasoning under 200 tokens. Do not explore multiple alternatives. Think concisely and output the solution immediately.
                  """;

        var userSuffix = !string.IsNullOrWhiteSpace(test.UserPromptSuffix)
            ? test.UserPromptSuffix
            : suiteContainer.DefaultUserPromptSuffix;

        var effectiveUserPrompt = !string.IsNullOrWhiteSpace(userSuffix)
            ? $"{test.Prompt.Trim()}\n\n{userSuffix.Trim()}"
            : test.Prompt.Trim();

        List<ChatMessage> messages = [new SystemChatMessage(effectiveSystemPrompt), new UserChatMessage(effectiveUserPrompt)];

        // Stage I: Primary Streaming Generation
        var currentMaxTokens = tuned.MaxOutputTokens;
        const int maxGenerationAttempts = 2;
        var totalGenTokens = 0;
        var fullResponse = string.Empty;
        var cleanResponse = string.Empty;
        double ttftMs = 0;
        double tps = 0;
        var genDuration = TimeSpan.Zero;

        for (var genAttempt = 1; genAttempt <= maxGenerationAttempts; genAttempt++)
        {
            cts.Token.ThrowIfCancellationRequested();

            var options = new ChatCompletionOptions
            {
                Temperature = tuned.Temperature,
                TopP = tuned.TopP,
                FrequencyPenalty = 0.15f,
                PresencePenalty = 0.15f,
                MaxOutputTokenCount = currentMaxTokens,
#pragma warning disable OPENAI001
                Seed = 42L
#pragma warning restore OPENAI001
            };

            ServerProbe.ConfigureStopSequences(options, serverMetadata.ModelFile);

            var sb = new StringBuilder(8192);
            var attemptTokens = 0;
            var isFirstToken = true;
            ChatFinishReason? finishReason = null;

            var sw = Stopwatch.StartNew();
            var ttftSw = Stopwatch.StartNew();

            try
            {
                var completionUpdates = chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken: cts.Token);

                await foreach (var update in completionUpdates.WithCancellation(cts.Token))
                {
                    foreach (var part in update.ContentUpdate)
                    {
                        if (!string.IsNullOrEmpty(part.Text))
                        {
                            if (isFirstToken)
                            {
                                ttftSw.Stop();
                                ttftMs = ttftSw.Elapsed.TotalMilliseconds;
                                isFirstToken = false;
                            }
                            sb.Append(part.Text);
                            attemptTokens++;
                        }
                    }
                    if (update.FinishReason.HasValue)
                    {
                        finishReason = update.FinishReason.Value;
                    }
                }

                sw.Stop();
                genDuration += sw.Elapsed;
                totalGenTokens += attemptTokens;

                fullResponse = sb.ToString();
                cleanResponse = ResponseSanitizer.StripReasoning(fullResponse);

                var totalSeconds = sw.Elapsed.TotalSeconds > 0 ? sw.Elapsed.TotalSeconds : 1.0;
                tps = attemptTokens / totalSeconds;

                var hasValidCode = cleanResponse.Contains("```csharp") || cleanResponse.Contains("```sql") || cleanResponse.Contains("```");
                var isReasoningMonologueOnly = string.IsNullOrWhiteSpace(cleanResponse);

                if (!isReasoningMonologueOnly && (hasValidCode || finishReason != ChatFinishReason.Length))
                {
                    break;
                }

                if (genAttempt < maxGenerationAttempts)
                {
                    currentMaxTokens += 1000;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    [RETRY] Output empty or truncated in reasoning (Attempt {genAttempt}/{maxGenerationAttempts}). Expanding budget to {currentMaxTokens} tokens...");
                    Console.ResetColor();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sw.Stop();
                genDuration += sw.Elapsed;
                totalGenTokens += attemptTokens;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    [WARN] Generation stream error on Attempt {genAttempt}: {ex.Message}");
                Console.ResetColor();
                if (genAttempt < maxGenerationAttempts)
                {
                    currentMaxTokens += 1000;
                }
            }
        }

        // Stage II: Fallback Handling for Empty Responses
        if (string.IsNullOrWhiteSpace(cleanResponse))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"    [{DateTime.Now:HH:mm:ss}] [FAIL] Empty response received for test '{test.Name}'.");
            Console.ResetColor();

            var metricsFailed = SystemTelemetry.ComputeMetrics(snapshot, totalGenTokens, costPerKwh);
            var failedResult = new TestResult(
                test,
                "ERROR: Empty response or generation timeout.",
                0.0,
                ttftMs,
                0,
                0.0,
                0.0,
                [],
                [.. test.Criteria.Select(c => c.Description)],
                totalGenTokens,
                metricsFailed,
                totalGenTokens,
                0,
                genDuration,
                TimeSpan.Zero
            );

            results.Add(failedResult);
            CheckpointManager.SaveCheckpoint(results, checkpointFilePath);
            number++;
            continue;
        }

        // Stage III: Evaluation & Scoring (MQES Enabled)
        try
        {
            TestResult result;

            if (noJudge)
            {
                var (effScore, normTps) = ModelScorer.Calculate(100, tps, totalGenTokens, profile);
                var metrics = SystemTelemetry.ComputeMetrics(snapshot, totalGenTokens, costPerKwh);
                result = new TestResult(test, fullResponse, tps, ttftMs, -1, effScore, normTps, [], [], totalGenTokens, metrics, totalGenTokens, 0, genDuration, TimeSpan.Zero);
            }
            else
            {
                // Evaluates using the dedicated judge client (Ollama, local llama-server, etc.)
                var (score, passed, failed, judgeTokens, cleanCode, judgeDuration) =
                    await EvaluationJudge.EvaluateResponseAsync(test, suiteContainer.DefaultJudgeSystemPrompt, fullResponse, judgeChatClient, judgeMaxTokens, cts.Token);

                var totalTokens = totalGenTokens + judgeTokens;
                var metrics = SystemTelemetry.ComputeMetrics(snapshot, totalTokens, costPerKwh);
                var (effScore, normTps) = ModelScorer.Calculate(score, tps, totalGenTokens, profile);

                result = new TestResult(test, cleanCode, tps, ttftMs, score, effScore, normTps, passed, failed, totalTokens, metrics, totalGenTokens, judgeTokens, genDuration, judgeDuration);
            }

            results.Add(result);
            CheckpointManager.SaveCheckpoint(results, checkpointFilePath);

            var qualityDisplay = result.Score < 0 ? "N/A" : $"{result.Score}/100 pts";
            var mqesDisplay = result.Score < 0 ? "N/A" : $"{result.EfficiencyScore:F1}/100";

            Console.WriteLine($"    [{DateTime.Now:HH:mm:ss}] [{number}/{selectedTests.Count}] Quality: {qualityDisplay} -> MQES: {mqesDisplay} | Speed: {tps:F1} t/s (Norm: {result.NormalizedTps:F1} t/s) | TTFT: {ttftMs:F0} ms | Time: {result.TotalDuration.TotalSeconds:F1}s (Gen: {result.GenerationDuration.TotalSeconds:F1}s | Judge: {result.JudgeDuration.TotalSeconds:F1}s)");
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"    └─ [Resources] CPU: {result.Metrics.ProcessCpuPct:F1}% | RAM (WS): {result.Metrics.ProcessWorkingSetMb:F1} MB (Heap: {result.Metrics.ManagedHeapMb:F1} MB) | Sys: {result.Metrics.SystemUsedGb:F1}/{result.Metrics.SystemTotalGb:F1} GB | GC Gen0/1/2: {result.Metrics.Gen0Collections}/{result.Metrics.Gen1Collections}/{result.Metrics.Gen2Collections} | Power: {result.Metrics.EstimatedWatts:F0} W | Energy: {result.Metrics.KWhConsumed * 1000:F2} Wh (${result.Metrics.CostUsd:F4})\n");
            Console.ResetColor();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"    [{DateTime.Now:HH:mm:ss}] [ERROR] Evaluation stage failed: {ex.Message}\n");
            Console.ResetColor();
        }

        number++;
    }
}
catch (OperationCanceledException) { }

// ============================================================================
// 6. Final Reporting and Exporting
// ============================================================================
runSw.Stop();
if (!cts.IsCancellationRequested)
{
    completedAll = true;
}

ConsoleReporter.PrintEnhancedSummaryReport(results, capabilities, runSw.Elapsed);

if (results.Count > 0)
{
    var timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    MarkdownExporter.Export(results, serverMetadata, $"benchmark_report_{timeStamp}.md");
    JsonExporter.Export(results, serverMetadata, $"benchmark_report_{timeStamp}.json");

    if (completedAll)
    {
        if (File.Exists(checkpointFilePath))
        {
            File.Delete(checkpointFilePath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[CHECKPOINT] Suite 100% completed. Temporary recovery checkpoint deleted.");
            Console.ResetColor();
        }
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n[CHECKPOINT PRESERVED] Tests recorded: {results.Count}. Resume with: MQES-Bench -r");
        Console.ResetColor();
    }
}

var elapsedStr = $"{(int)runSw.Elapsed.TotalHours:D2}:{runSw.Elapsed.Minutes:D2}:{runSw.Elapsed.Seconds:D2}";

Console.WriteLine($"Completed at {DateTime.Now:yyyy-MM-dd HH:mm:ss} (elapsed: {elapsedStr})\n"); 