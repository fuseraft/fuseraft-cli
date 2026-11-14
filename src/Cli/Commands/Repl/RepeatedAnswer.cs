using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Finds a turn's closing text that only says again what the model already answered. Models answer, call a bookkeeping
/// tool (update the todo list, check the context), then answer a second time; live, grok-4.7 did it in 3 of 5 turns.
/// Only bookkeeping may come between the two, since it changes nothing: an answer after real work is never dropped.
/// </summary>
internal static partial class RepeatedAnswer
{
    internal static readonly HashSet<string> BookkeepingTools = new(StringComparer.Ordinal)
    {
        "todo_write", "repl_session_get_context_status", "repl_session_compact_context",
    };

    private const int MinWords = 5;
    private const double MinShared = 0.6;

    /// <summary>The closing text to drop, or null when the turn's last words are new.</summary>
    internal static string? Find(List<ChatResponseUpdate> updates)
    {
        // A stream ends with an update that only reports token usage; it would read as an empty last message.
        var messages = new List<ChatMessage>();
        messages.AddMessages(updates.Where(u => u.Contents.Any(c => c is not UsageContent)));
        if (messages.Count < 3 || messages[^1] is not { Role: var role } last || role != ChatRole.Assistant ||
            last.Contents.OfType<FunctionCallContent>().Any() || string.IsNullOrWhiteSpace(last.Text))
            return null;

        for (var i = messages.Count - 2; i >= 0; i--)
        {
            var m = messages[i];
            if (m.Role == ChatRole.Tool) continue;
            if (m.Role != ChatRole.Assistant) return null;
            if (m.Contents.OfType<FunctionCallContent>().Any(c => !BookkeepingTools.Contains(c.Name))) return null;
            if (!string.IsNullOrWhiteSpace(m.Text))
                return Repeats(last.Text, m.Text) ? last.Text : null;
        }
        return null;
    }

    // Most of the later text's words were in the earlier answer: a rewording, or the same answer again.
    internal static bool Repeats(string later, string earlier)
    {
        var laterWords   = Words(later);
        var earlierWords = Words(earlier).ToHashSet();
        if (laterWords.Count < MinWords || earlierWords.Count < MinWords) return false;
        return (double)laterWords.Count(earlierWords.Contains) / laterWords.Count >= MinShared;
    }

    private static List<string> Words(string text) =>
        [.. WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value)];

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex WordPattern();
}
