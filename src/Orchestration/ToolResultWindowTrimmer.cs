using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

/// <summary>
/// Enforces a sliding window over tool-result messages in a context list.
///
/// <para>
/// When the cumulative estimated token cost of all <see cref="FunctionResultContent"/>
/// items in <paramref name="context"/> exceeds <see cref="ContextBudgetConfig.MaxToolResultTokens"/>,
/// the oldest results beyond the last <see cref="ContextBudgetConfig.InTurnToolWindow"/>
/// are replaced with one-line tombstones of the form:
/// <c>[tool result for read_file(graph.py) — evicted after tool window exceeded]</c>
/// </para>
///
/// <para>
/// The trimmer operates only on the slice passed to the LLM; the canonical shared history
/// maintained by <c>AgentOrchestrator</c> is never modified. This preserves the full audit
/// trail while preventing tool-result token accumulation from growing unboundedly within
/// a single agent invocation.
/// </para>
/// </summary>
public static class ToolResultWindowTrimmer
{
    // Characters per token estimate — consistent with the rest of the codebase.
    private const int CharsPerToken = 4;

    /// <summary>
    /// Returns a new list with old tool results tombstoned when the budget is exceeded,
    /// or returns <paramref name="context"/> unchanged when trimming is not needed.
    /// </summary>
    public static IList<ChatMessage> Apply(IList<ChatMessage> context, ContextBudgetConfig budget)
    {
        if (budget.MaxToolResultTokens <= 0) return context;

        // Collect all ChatMessage indices that contain at least one FunctionResultContent,
        // along with their estimated token cost. Walk in order so we can tombstone the oldest.
        var resultMessages = new List<(int MsgIdx, int EstTokens)>();
        int totalEstTokens = 0;

        for (int i = 0; i < context.Count; i++)
        {
            var msg         = context[i];
            int resultChars = msg.Contents
                .OfType<FunctionResultContent>()
                .Sum(fr => fr.Result?.ToString()?.Length ?? 0);
            if (resultChars > 0)
            {
                int est = resultChars / CharsPerToken;
                resultMessages.Add((i, est));
                totalEstTokens += est;
            }
        }

        // Fast path — nothing to trim.
        if (totalEstTokens <= budget.MaxToolResultTokens) return context;

        // Determine how many of the oldest results to evict.
        // Always keep at least the last InTurnToolWindow results verbatim.
        int retainCount = Math.Max(0, budget.InTurnToolWindow);
        int evictUpTo   = Math.Max(0, resultMessages.Count - retainCount);
        if (evictUpTo == 0) return context;

        var evictIndices = new HashSet<int>(
            resultMessages.Take(evictUpTo).Select(r => r.MsgIdx));

        // Build the trimmed list, replacing evicted messages with a tombstone.
        var trimmed = new List<ChatMessage>(context.Count);
        foreach (var msg in context)
        {
            int idx = trimmed.Count; // index in source context
            if (evictIndices.Contains(trimmed.Count))
            {
                // Replace tool result content with tombstones; keep function-call
                // content intact so the model can still see what was requested.
                var tombstoned = new List<AIContent>();
                foreach (var item in msg.Contents)
                {
                    if (item is FunctionResultContent fr)
                    {
                        // Build a compact tombstone that names the tool and call ID.
                        var callId = fr.CallId ?? "unknown";
                        tombstoned.Add(new FunctionResultContent(callId,
                            $"[tool result — evicted after tool window exceeded]"));
                    }
                    else
                    {
                        tombstoned.Add(item);
                    }
                }
                var replacement = new ChatMessage(msg.Role, tombstoned)
                {
                    AuthorName = msg.AuthorName
                };
                trimmed.Add(replacement);
            }
            else
            {
                trimmed.Add(msg);
            }
        }

        return trimmed;
    }
}
