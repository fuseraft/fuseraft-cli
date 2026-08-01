using System.Text;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Context;

/// <summary>
/// Enforces a sliding window over tool-result messages in a context list.
///
/// <para>
/// When the cumulative estimated token cost of all <see cref="FunctionResultContent"/>
/// items in <paramref name="context"/> exceeds <see cref="ContextBudgetConfig.MaxToolResultTokens"/>,
/// the oldest results beyond the last <see cref="ContextBudgetConfig.InTurnToolWindow"/>
/// are replaced with one-line tombstones of the form:
/// <c>[tool result — evicted after tool window exceeded]</c>
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
    // Keep previews small so many evictions do not create a second token spike.
    private const int PreviewChars = 160;
    private const int PreviewToolLimit = 3;
    private const int MaxManifestEvictedLabels = 5;

    internal const string TombstonePrefix = "[tool result — evicted";

    private static readonly string[] s_labelKeys = ["path", "command", "query", "content", "name"];

    /// <summary>
    /// Returns a new list with old tool results tombstoned when the budget is exceeded,
    /// or returns <paramref name="context"/> unchanged when trimming is not needed.
    ///
    /// <para>
    /// Each tombstone names the evicted tool and includes a short content preview so
    /// the model can judge whether to re-read with a targeted range, without fetching
    /// the full result again.
    /// </para>
    /// </summary>
    public static IList<ChatMessage> Apply(IList<ChatMessage> context, ContextBudgetConfig budget)
    {
        var (trimmed, _, _) = ApplyCore(context, budget);
        return trimmed;
    }

    /// <summary>
    /// Applies the tool-result window budget and returns a context manifest alongside
    /// the trimmed message list. The manifest is non-null only when evictions occurred;
    /// it lists active tool results and superseded (evicted) ones so the model knows
    /// which reads are still available and which must be re-issued with targeted ranges.
    /// </summary>
    public static (IList<ChatMessage> Messages, string? Manifest) ApplyWithManifest(
        IList<ChatMessage> context,
        ContextBudgetConfig budget)
    {
        var (trimmed, callLabels, evicted) = ApplyCore(context, budget);
        if (!evicted) return (trimmed, null);

        var activeCount = 0;
        var superseded = new List<string>();

        foreach (var msg in trimmed)
        {
            foreach (var fr in msg.Contents.OfType<FunctionResultContent>())
            {
                var callId = fr.CallId ?? "unknown";
                var label = callLabels.GetValueOrDefault(callId, callId);
                var result = fr.Result?.ToString() ?? "";

                if (result.StartsWith(TombstonePrefix, StringComparison.Ordinal))
                {
                    if (superseded.Count < MaxManifestEvictedLabels)
                        superseded.Add(label);
                }
                else
                {
                    activeCount++;
                }
            }
        }

        if (activeCount == 0 && superseded.Count == 0) return (trimmed, null);

        var sb = new StringBuilder();
        sb.AppendLine("[Context Manifest]");
        sb.AppendLine();
        sb.AppendLine($"Tool results retained: {activeCount}");
        sb.AppendLine($"Older tool results evicted: {trimmed.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Count(fr => (fr.Result?.ToString() ?? string.Empty).StartsWith(TombstonePrefix, StringComparison.Ordinal))}");

        if (superseded.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Most recent evicted:");
            foreach (var s in superseded) sb.AppendLine($"- {s}");
        }

        sb.AppendLine();
        sb.Append("Re-read targeted ranges if needed.");
        return (trimmed, sb.ToString().TrimEnd());
    }

    // Returns (trimmed list, callLabels map, evicted flag).
    // When evicted is false, trimmed is the same reference as context and callLabels holds
    // the map built during the scan (useful to ApplyWithManifest without a second pass).
    private static (IList<ChatMessage> Trimmed, Dictionary<string, string> CallLabels, bool Evicted)
        ApplyCore(IList<ChatMessage> context, ContextBudgetConfig budget)
    {
        if (budget.MaxToolResultTokens <= 0) return (context, [], false);

        // Pass 1: collect budget info and build callId → label map for enriched tombstones.
        var resultMessages = new List<(int MsgIdx, int EstTokens)>();
        int totalEstTokens = 0;
        var callLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < context.Count; i++)
        {
            var msg = context[i];

            foreach (var call in msg.Contents.OfType<FunctionCallContent>())
                if (call.CallId is not null)
                    callLabels[call.CallId] = FormatCallLabel(call);

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
        if (totalEstTokens <= budget.MaxToolResultTokens) return (context, callLabels, false);

        // Evict only as many oldest results as needed to get back under budget.
        // Always keep at least the last InTurnToolWindow results verbatim.
        int retainCount = Math.Max(0, budget.InTurnToolWindow);
        int protectedStart = Math.Max(0, resultMessages.Count - retainCount);

        var evictIndices = new HashSet<int>();
        int runningTokens = totalEstTokens;
        for (int i = 0; i < protectedStart && runningTokens > budget.MaxToolResultTokens; i++)
        {
            evictIndices.Add(resultMessages[i].MsgIdx);
            runningTokens -= resultMessages[i].EstTokens;
        }

        if (evictIndices.Count == 0) return (context, callLabels, false);

        // Pass 2: build trimmed list with enriched tombstones.
        var trimmed = new List<ChatMessage>(context.Count);
        int previewedResults = 0;
        foreach (var msg in context)
        {
            if (evictIndices.Contains(trimmed.Count))
            {
                // Replace tool result content with tombstones; keep function-call
                // content intact so the model can still see what was requested.
                var tombstoned = new List<AIContent>();
                foreach (var item in msg.Contents)
                {
                    if (item is FunctionResultContent fr)
                    {
                        var callId = fr.CallId ?? "unknown";
                        var label = callLabels.GetValueOrDefault(callId, callId);
                        var content = fr.Result?.ToString() ?? "";
                        var excerpt = previewedResults < PreviewToolLimit
                            ? BuildPreview(content)
                            : string.Empty;

                        var tombstone = string.IsNullOrEmpty(excerpt)
                            ? $"{TombstonePrefix}: {label}. Re-read with targeted ranges if needed.]"
                            : $"{TombstonePrefix}: {label}. Preview: \"{excerpt}\". Re-read with targeted ranges if needed.]";

                        tombstoned.Add(new FunctionResultContent(callId, tombstone));
                        previewedResults++;
                    }
                    else
                    {
                        tombstoned.Add(item);
                    }
                }
                trimmed.Add(new ChatMessage(msg.Role, tombstoned) { AuthorName = msg.AuthorName });
            }
            else
            {
                trimmed.Add(msg);
            }
        }

        return (trimmed, callLabels, true);
    }

    private static string BuildPreview(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;

        var normalized = content.Trim();
        return normalized.Length > PreviewChars
            ? normalized[..PreviewChars].TrimEnd() + "…"
            : normalized;
    }

    private static string FormatCallLabel(FunctionCallContent call)
    {
        var name = call.Name ?? "tool";
        if (call.Arguments is null || call.Arguments.Count == 0) return name;

        foreach (var key in s_labelKeys)
        {
            if (call.Arguments.TryGetValue(key, out var val) && val is string s)
                return $"{name}({(s.Length > 50 ? s[..50] + "…" : s)})";
        }

        var first = call.Arguments.Values.FirstOrDefault()?.ToString() ?? "";
        return string.IsNullOrEmpty(first) ? name
            : $"{name}({(first.Length > 50 ? first[..50] + "…" : first)})";
    }
}
