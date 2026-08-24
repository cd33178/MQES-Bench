using System.Text.RegularExpressions;

namespace MQESBench.Services;

public static class ResponseSanitizer
{
    /// <summary>
    /// Strips thinking tags, internal reasoning, and special end-of-sequence tokens.
    /// </summary>
    public static string StripReasoning(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = text;

        // 1. Clean up to=self formatting artifacts
        if (cleaned.Contains("to=self", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(cleaned, @"to=self<\|message\|>[\s\S]*?(?:<\|eom\|><\|start\|>assistant to=user<\|message\|>|<\|start\|>assistant<\|message\|>|assistant to=user<\|message\|>)", RegexOptions.Compiled);
            if (match.Success)
            {
                cleaned = cleaned.Substring(match.Index + match.Length).Trim();
            }
        }

        // 2. Clean up normally closed <think> blocks
        cleaned = Regex.Replace(cleaned, @"<think>[\s\S]*?</think>", string.Empty, RegexOptions.Compiled).Trim();

        // 3. Rescue if an unclosed <think> block remained due to token truncation
        if (cleaned.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            var closeIdx = cleaned.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (closeIdx >= 0)
            {
                cleaned = cleaned[(closeIdx + 8)..].Trim();
            }
            else
            {
                var codeMatch = Regex.Match(cleaned, @"```(?:csharp|sql|cpp)?[\s\S]*?(?:```|$)", RegexOptions.Compiled);
                if (codeMatch.Success)
                {
                    cleaned = codeMatch.Value;
                    if (!cleaned.EndsWith("```"))
                    {
                        cleaned += "\n```";
                    }
                }
                else
                {
                    cleaned = string.Empty;
                }
            }
        }

        // 4. Clean up residual special tokens
        cleaned = Regex.Replace(cleaned, @"<\|(?:eot|eom|endoftext|im_end|im_start|message|start)\|>", string.Empty, RegexOptions.Compiled).Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? text.Trim() : cleaned;
    }
}