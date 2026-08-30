using System.Diagnostics;
using System.Text.RegularExpressions;
using MQESBench.Models;
using OpenAI.Chat;

namespace MQESBench.Services;

public static class EvaluationJudge
{
    private const string DefaultGenericJudgePrompt = """
        You are a strict, highly accurate technical evaluation judge.
        Evaluate the provided technical text strictly against the criterion.
        
        CRITICAL EVALUATION GUIDELINES:
        1. Verify that the implementation satisfies the architectural intent and correctness requirements.
        2. Do NOT output internal thinking or reasoning steps.
        3. You must immediately output your final decision on the very first line as:
           VERDICT: PASS
           or
           VERDICT: FAIL

           Followed by a brief one-sentence reason.
        """;

    public static async Task<(int Score, List<string> Passed, List<string> Failed, int JudgeTokens, string CleanResponse, TimeSpan JudgeDuration)> EvaluateResponseAsync(
        TestCase test,
        string? defaultSuiteJudgePrompt,
        string rawResponse,
        ChatClient judgeClient,
        CancellationToken ct = default)
    {
        var judgeSw = Stopwatch.StartNew();
        var score = 0;
        var judgeTokens = 0;
        List<string> passed = [];
        List<string> failed = [];

        // 1. Resolve Effective Judge Prompt inside the service (Test override -> Suite default -> Generic Fallback)
        var effectiveJudgePrompt = !string.IsNullOrWhiteSpace(test.JudgeSystemPrompt)
            ? test.JudgeSystemPrompt
            : (!string.IsNullOrWhiteSpace(defaultSuiteJudgePrompt)
                ? defaultSuiteJudgePrompt
                : DefaultGenericJudgePrompt);

        // 2. Clean candidate response before evaluation
        var cleanResponseToEvaluate = ResponseSanitizer.StripReasoning(rawResponse);

        // 3. Evaluate criteria loop
        foreach (var criterion in test.Criteria)
        {
            List<ChatMessage> judgeMessages = [
                new SystemChatMessage(effectiveJudgePrompt),
                new UserChatMessage($"""
                    [TEXT TO EVALUATE]
                    {cleanResponseToEvaluate}

                    [CRITERION TO VERIFY]
                    {criterion.Description}
                    """)
            ];

            var isPassed = false;
            const int maxRetries = 3;
            var currentMaxTokens = 800;

            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var judgeOptions = new ChatCompletionOptions
                {
                    Temperature = 0.0f,
                    TopP = 0.9f,
                    FrequencyPenalty = 0.0f,
                    PresencePenalty = 0.0f,
                    MaxOutputTokenCount = currentMaxTokens,
#pragma warning disable OPENAI001
                    Seed = 42L
#pragma warning restore OPENAI001
                };
                judgeOptions.StopSequences.Add("<|im_end|>");
                judgeOptions.StopSequences.Add("<|endoftext|>");
                judgeOptions.StopSequences.Add("<|eot|>");

                try
                {
                    await Task.Delay(100, ct);

                    ChatCompletion judgeCompletion = await judgeClient.CompleteChatAsync(judgeMessages, judgeOptions, cancellationToken: ct);
                    var rawAnswer = judgeCompletion.Content.FirstOrDefault()?.Text ?? string.Empty;

                    var consumed = judgeCompletion.Usage?.OutputTokenCount ?? 0;
                    if (consumed == 0 && !string.IsNullOrWhiteSpace(rawAnswer))
                    {
                        consumed = Math.Max(1, rawAnswer.Length / 4);
                    }
                    judgeTokens += consumed;

                    var cleanAnswer = ResponseSanitizer.StripReasoning(rawAnswer);
                    cleanAnswer = Regex.Replace(cleanAnswer, @"to=self.*?(?=VERDICT|\n|$)", "", RegexOptions.Singleline);
                    var targetText = string.IsNullOrWhiteSpace(cleanAnswer) ? rawAnswer : cleanAnswer;

                    var upper = targetText.ToUpperInvariant();
                    var hasExplicitVerdict = upper.Contains("VERDICT: PASS") || upper.Contains("VERDICT: FAIL");
                    var isTruncated = judgeCompletion.FinishReason == ChatFinishReason.Length;

                    if (hasExplicitVerdict && !isTruncated)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"   [JUDGE DEBUG (Attempt {attempt})]: {targetText.Replace("\n", " ").Trim()}");
                        Console.ResetColor();

                        isPassed = upper.Contains("VERDICT: PASS") || (upper.Contains("PASS") && !upper.Contains("FAIL"));
                        break;
                    }

                    if (attempt < maxRetries)
                    {
                        currentMaxTokens += 500;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"   [RETRY] Judge output truncated or inconclusive (Attempt {attempt}/{maxRetries}). Expanding budget to {currentMaxTokens} tokens...");
                        Console.ResetColor();
                        continue;
                    }

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"   [JUDGE DEBUG (Final Truncated Attempt {attempt})]: {targetText.Replace("\n", " ").Trim()}");
                    Console.ResetColor();

                    isPassed = upper.Contains("VERDICT: PASS") || (upper.Contains("PASS") && !upper.Contains("FAIL"));
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"   [WARN] Judge Exception (Attempt {attempt}): {ex.Message}");
                    Console.ResetColor();

                    if (attempt < maxRetries)
                    {
                        currentMaxTokens += 500;
                    }
                }
            }

            if (isPassed)
            {
                score += criterion.Weight;
                passed.Add(criterion.Description);
            }
            else
            {
                failed.Add(criterion.Description);
            }
        }

        judgeSw.Stop();
        return (score, passed, failed, judgeTokens, cleanResponseToEvaluate, judgeSw.Elapsed);
    }
}