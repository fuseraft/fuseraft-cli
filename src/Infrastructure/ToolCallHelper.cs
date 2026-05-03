using Microsoft.Extensions.AI;
using fuseraft.Core;

namespace fuseraft.Infrastructure;

/// <summary>
/// Shared utilities for summarising tool-call arguments into a compact display string.
/// Used by orchestrators when building <see cref="Core.Models.ToolCallRecord"/> instances
/// and by <see cref="AgentFactory"/> when firing real-time <c>ToolCalling</c> events.
/// </summary>
public static class ToolCallHelper
{
    /// <summary>
    /// Produces a compact <c>key=value</c> string from <see cref="AIFunctionArguments"/>.
    /// Prefers well-known high-signal keys; falls back to the first key in the dictionary.
    /// Returns <c>null</c> when <paramref name="args"/> is null or empty.
    /// </summary>
    public static string? SummarizeArgs(AIFunctionArguments? args)
        => args is null ? null : SummarizeCore(args);

    /// <summary>
    /// Produces a compact <c>key=value</c> string from a tool-call argument dictionary.
    /// Prefers well-known high-signal keys; falls back to the first key in the dictionary.
    /// Returns <c>null</c> when <paramref name="args"/> is null or empty.
    /// </summary>
    public static string? SummarizeArgs(IDictionary<string, object?>? args)
        => args is null ? null : SummarizeCore(args);

    private static string? SummarizeCore(IEnumerable<KeyValuePair<string, object?>> args)
    {
        var list = args as IReadOnlyCollection<KeyValuePair<string, object?>>
                ?? args.ToList();
        if (list.Count == 0) return null;

        ReadOnlySpan<string> priority = ["path", "command", "script", "url", "key", "query", "message", "branch"];
        foreach (var key in priority)
        {
            var match = list.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Value is not null)
                return $"{key}={StringHelpers.Truncate(System.Net.WebUtility.HtmlDecode(match.Value.ToString() ?? string.Empty), 60)}";
        }

        var first = list.First();
        return $"{first.Key}={StringHelpers.Truncate(System.Net.WebUtility.HtmlDecode(first.Value?.ToString() ?? string.Empty), 60)}";
    }
}
