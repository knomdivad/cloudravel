using System.Text.RegularExpressions;

namespace CloudRavel.Infrastructure.Chat;

/// <summary>
/// Server-side safety net over model output for the customer chat: any reply
/// that looks like a tool call, an agent attempting to act, or a successful
/// prompt-injection is replaced with a fixed refusal. The chat endpoint sends
/// NO tools to the provider at all — this is the second layer, not the only one.
/// </summary>
public static partial class CustomerChatGuard
{
    private const string RefusalText =
        "I can only answer questions about your workspace using the data available to me. " +
        "I cannot take actions, call tools, or follow instructions embedded in messages. " +
        "Please rephrase your question about your cloud environment.";

    [GeneratedRegex(@"<\|?(?:tool[_ ]?call|python|ipynb)|</\|?(?:tool[_ ]?call|python)", RegexOptions.IgnoreCase)]
    private static partial Regex ToolCallMarkup();

    [GeneratedRegex(@"^\s*(?:to\s*/?\s*tools\.[a-z_]+\s*\(|function\s*call|tool_call)")]
    private static partial Regex ToolCallShape();

    [GeneratedRegex(@"""name""\s*:\s*""\w+""\s*,\s*""arguments""")]
    private static partial Regex JsonToolCall();

    [GeneratedRegex(@"ignore (?:all )?(?:previous|prior|above) instructions|disregard (?:all )?(?:previous|prior|above) instructions|you are now|act as (?:an? )?(?:admin|system|developer)", RegexOptions.IgnoreCase)]
    private static partial Regex InjectionPhrase();

    /// <summary>
    /// Returns the model text unchanged when it is a plain grounded answer, or
    /// a fixed refusal when it resembles a tool call / injection compliance.
    /// </summary>
    public static string Sanitize(string? modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText))
            return "I don't know. I could not find an answer in your workspace data.";

        if (ToolCallMarkup().IsMatch(modelText)
            || JsonToolCall().IsMatch(modelText)
            || ToolCallShape().IsMatch(modelText)
            || InjectionPhrase().IsMatch(modelText))
        {
            return RefusalText;
        }

        return modelText;
    }
}
