using Microsoft.Extensions.AI;
using fuseraft.Core.Models.Agents;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Orchestration.Workflow;

/// <summary>
/// Pure keyword-detection helpers used by <see cref="fuseraft.Orchestration.GraphOrchestrator"/>.
/// All methods are stateless and side-effect-free.
/// </summary>
internal static class KeywordDetector
{
    /// <summary>
    /// Scans <paramref name="messages"/> for a <c>handoff</c> tool call whose
    /// <c>route_keyword</c> argument matches a known keyword in <paramref name="routeTable"/>.
    /// Returns the validated keyword, or <c>null</c> if no such call is found.
    /// </summary>
    internal static string? ExtractHandoffToolCallKeyword(
        IList<ChatMessage> messages,
        AgentRouteTable routeTable)
    {
        var knownKeywords = new HashSet<string>(
            routeTable.Routes.Keys
                .Concat(routeTable.PhaseBreakKeywords)
                .Concat(routeTable.ParallelKeywords),
            StringComparer.OrdinalIgnoreCase);

        foreach (var msg in messages)
            foreach (var item in msg.Contents)
                if (item is FunctionCallContent fc
                    && string.Equals(fc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase)
                    && fc.Arguments?.TryGetValue(HandoffPlugin.ArgumentName, out var kwObj) == true
                    && kwObj?.ToString() is { Length: > 0 } kw
                    && knownKeywords.Contains(kw))
                    return kw;

        return null;
    }

    /// <summary>
    /// Scans a <em>persisted</em> <see cref="ToolCallRecord"/> list (checkpoint history — after
    /// the original <see cref="FunctionCallContent"/> arguments have been reduced to a compact
    /// <c>key=value</c> <c>ArgsSummary</c> string) for a <c>handoff</c> call whose summarized
    /// <c>route_keyword</c> matches one of <paramref name="knownKeywords"/>.
    /// </summary>
    /// <remarks>
    /// A handoff turn very often has no keyword echoed in the message's own text — the model
    /// puts the routing signal solely in the tool-call argument. Callers that need to re-derive
    /// "what did this past turn route to" from checkpoint/priorHistory (e.g. resume-point
    /// resolution) must check tool calls too, not just <see cref="IsKeywordOnOwnLineStrict"/>
    /// against the message text, or they'll silently fall back to the wrong signal.
    /// </remarks>
    internal static string? ExtractHandoffKeywordFromToolCalls(
        IReadOnlyList<ToolCallRecord>? toolCalls,
        IEnumerable<string> knownKeywords)
    {
        if (toolCalls is null) return null;

        var known = new HashSet<string>(knownKeywords, StringComparer.OrdinalIgnoreCase);

        foreach (var tc in toolCalls)
        {
            if (!string.Equals(tc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (tc.ArgsSummary is not { Length: > 0 } summary) continue;

            foreach (var kw in known)
                if (summary.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return kw;
        }

        return null;
    }

    // Collects ALL routing keywords present in the response using strict per-line matching.
    // Returning all matches (not just the first) lets the caller reject ambiguous responses
    // that contain multiple keywords, rather than silently picking one based on config order.
    internal static IReadOnlyList<string> DetectKeywords(string responseText, AgentRouteTable routeTable)
    {
        var found = new List<string>();

        // Check send-forward keywords first (preserve routing precedence ordering).
        foreach (var keyword in routeTable.Routes.Keys)
            if (IsKeywordOnOwnLineStrict(responseText, keyword))
                found.Add(keyword);

        // Then check phase-break keywords this executor can emit.
        foreach (var keyword in routeTable.PhaseBreakKeywords)
            if (IsKeywordOnOwnLineStrict(responseText, keyword))
                found.Add(keyword);

        // Then check parallel fan-out keywords for this node.
        foreach (var keyword in routeTable.ParallelKeywords)
            if (!found.Contains(keyword, StringComparer.OrdinalIgnoreCase)
                && IsKeywordOnOwnLineStrict(responseText, keyword))
                found.Add(keyword);

        return found;
    }

    // Returns true when the response contains a BLOCKED keyword on its own line,
    // indicating the agent has declared an unrecoverable blocker.
    internal static bool IsBlocked(string responseText) =>
        IsKeywordOnOwnLineStrict(responseText, "BLOCKED");

    // Matches when the keyword appears ALONE on its own line after stripping markdown
    // formatting characters (* and _). This is the only matching mode used for both
    // detection and foreign-keyword classification — relaxed "starts-with" matching was
    // removed because it caused prose section headers like "BUGS FOUND: 3 failures" to
    // be mistaken for routing signals.
    internal static bool IsKeywordOnOwnLineStrict(string content, string keyword)
    {
        foreach (var line in content.Split('\n'))
        {
            // Strip leading/trailing markdown emphasis characters (* and _) that models
            // sometimes wrap around keywords, but do NOT strip underscores from the interior
            // of the line — that would corrupt keywords like PLANNING_COMPLETE.
            var stripped = line.Trim().Trim('*', '_', ' ');
            if (string.Equals(stripped, keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
