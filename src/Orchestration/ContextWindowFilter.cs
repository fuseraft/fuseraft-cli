using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

/// <summary>
/// Applies a <see cref="ContextWindowConfig"/> to a conversation history, returning a
/// filtered slice suitable for passing to an agent's <c>RunAsync</c> call.
///
/// Filters are applied in this order:
/// <list type="number">
///   <item><see cref="ContextWindowConfig.TextOnly"/> / <see cref="ContextWindowConfig.ExcludeAgents"/>
///     — strip tool messages and/or specific agents' output.</item>
///   <item><see cref="ContextWindowConfig.MaxTailMessages"/> — keep only the last N messages.</item>
/// </list>
///
/// The original history list is never mutated.
/// </summary>
public static class ContextWindowFilter
{
    /// <summary>
    /// Returns a filtered view of <paramref name="history"/> according to
    /// <paramref name="window"/>. Returns <paramref name="history"/> unchanged
    /// when <paramref name="window"/> is <c>null</c>.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Apply(
        IEnumerable<ChatMessage> history,
        ContextWindowConfig? window)
    {
        if (window is null) return history.ToList();

        IEnumerable<ChatMessage> messages = history;

        // Step 1: Strip tool messages.
        //
        // Triggered by TextOnly OR a non-empty ExcludeAgents list.
        // When ExcludeAgents is set we must also strip ChatRole.Tool result messages
        // even when TextOnly is false, because tool results are not attributed to a
        // specific agent. Leaving them in after stripping the corresponding call frames
        // would produce a malformed context with orphaned results.
        //
        // Mixed assistant messages (text + tool-call content in the same message) are
        // reduced to their text-only contents rather than kept as-is. Keeping the full
        // message while stripping the corresponding ChatRole.Tool result would leave
        // orphaned tool_use ids and cause a 400 from strict providers (e.g. Bedrock).
        if (window.TextOnly || window.ExcludeAgents.Count > 0)
        {
            var filtered = new List<ChatMessage>();
            foreach (var m in messages)
            {
                if (m.Role == ChatRole.User)
                {
                    filtered.Add(m);
                    continue;
                }

                if (m.Role != ChatRole.Assistant)
                    continue; // drop ChatRole.Tool result messages

                var textContents = m.Contents
                    .OfType<TextContent>()
                    .Where(t => !string.IsNullOrEmpty(t.Text))
                    .ToList<AIContent>();

                if (textContents.Count == 0)
                    continue; // pure tool-call frame (or empty-text frame) — drop

                var hasToolCalls = m.Contents.OfType<FunctionCallContent>().Any();
                if (!hasToolCalls)
                {
                    filtered.Add(m); // already text-only — keep as-is
                    continue;
                }

                // Mixed message: strip tool-call content, keep only text.
                // This prevents orphaned tool_use ids when the corresponding
                // ChatRole.Tool result messages are not included in the slice.
                filtered.Add(new ChatMessage(ChatRole.Assistant, textContents) { AuthorName = m.AuthorName });
            }
            messages = filtered;
        }

        // Step 2: Exclude messages authored by listed agents.
        // Both text-bearing and (after step 1) any remaining assistant messages
        // authored by the excluded agents are removed.
        if (window.ExcludeAgents.Count > 0)
        {
            messages = messages.Where(m =>
                m.Role != ChatRole.Assistant ||
                !window.ExcludeAgents.Contains(
                    m.AuthorName ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase));
        }

        // Step 3: Turn-age limit — keep only messages from the last N agent turns.
        // An "agent turn" is the span ending at each assistant message. We walk backward
        // counting assistant messages; the first index where the count equals MaxTurnAge
        // becomes the cut-point so that only the last N turns survive.
        var list = messages.ToList();

        if (window.MaxTurnAge > 0 && list.Count > 0)
        {
            int assistantTurnsSeen = 0;
            int cutIndex = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Role == ChatRole.Assistant)
                    assistantTurnsSeen++;
                if (assistantTurnsSeen >= window.MaxTurnAge)
                {
                    cutIndex = i;
                    break;
                }
            }
            // Only trim when we actually found enough turns; otherwise keep everything.
            if (assistantTurnsSeen >= window.MaxTurnAge && cutIndex > 0)
                list = list.Skip(cutIndex).ToList();
        }

        // Step 4: Tail limit — keep only the last N messages.
        if (window.MaxTailMessages > 0 && list.Count > window.MaxTailMessages)
            return list.Skip(list.Count - window.MaxTailMessages).ToList();

        return list;
    }

    // Maximum number of characters to replay from a single non-summary assistant message.
    // Agents sometimes produce verbose stream-of-consciousness reasoning text (3–5k output
    // tokens). When that text is replayed verbatim in every subsequent turn it causes
    // compaction summaries to grow each cycle and in-turn input tokens to balloon (450k+).
    // Compaction summaries (IsCompactionSummary) are already compact and are never truncated.
    private const int MaxReplayChars = 2_000;

    /// <summary>
    /// Returns the content string to replay for <paramref name="message"/> into the next
    /// <c>StreamAsync</c> call's history. Verbose non-summary assistant messages are
    /// truncated at <see cref="MaxReplayChars"/> to prevent compounding context growth.
    /// </summary>
    public static string TruncateReplayContent(AgentMessage message)
    {
        var content = message.Content ?? string.Empty;

        if (message.IsCompactionSummary
            || message.Role != "assistant"
            || content.Length <= MaxReplayChars)
            return content;

        return content[..MaxReplayChars] +
               $"\n[...truncated — {content.Length - MaxReplayChars:N0} chars omitted to reduce context size...]";
    }
}
