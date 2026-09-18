using System.Text.Json;
using Microsoft.Extensions.AI;

namespace fuseraft.Core;

/// <summary>
/// Extracts the text of a tool/function invocation result — <see cref="AIFunction"/>'s
/// <c>InvokeAsync</c> return value, or a <c>FunctionResultContent.Result</c> read back from
/// history — regardless of whether the framework kept the raw CLR value or (the common case)
/// routed it through a JSON round-trip and handed back a <see cref="JsonElement"/> instead.
///
/// <para>
/// A bare <c>result is string s</c> check silently misses the <see cref="JsonElement"/> case
/// entirely — not an edge case: it's what a plain string-returning tool method's result looks
/// like by the time it reaches a <c>DelegatingAIFunction</c>'s <c>InvokeCoreAsync</c> or a
/// replayed <c>FunctionResultContent</c>. Each of the three checks below independently hit this
/// exact trap before being fixed here — <see cref="fuseraft.Infrastructure.Plugins.ToolResultOffloadFilter"/>
/// (oversized-result offload never fired), <c>AgentContextCompactionFilters</c>'s per-result
/// truncation stage (never fired for tool results), and <c>ContextWindowFilter</c>'s
/// consumed-read elision (same). Route every such check through here instead of a local
/// re-implementation, so the next one only has to be fixed once.
/// </para>
/// </summary>
internal static class ToolResultText
{
    /// <summary>
    /// Returns the text of <paramref name="result"/> when it is a string or a JSON string
    /// element, or <c>null</c> for anything else (including JSON numbers/objects/arrays/null) —
    /// callers that want a stringified form of literally anything should use
    /// <see cref="ToStringOrDefault"/> instead.
    /// </summary>
    public static string? AsStringOrNull(object? result) => result switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null,
    };

    /// <summary>
    /// Returns the text of <paramref name="result"/> for any shape — a string or JSON string
    /// element verbatim, anything else via <see cref="object.ToString"/> (e.g. a JSON number
    /// element renders as its literal digits). Empty string for <c>null</c>.
    /// </summary>
    public static string ToStringOrDefault(object? result) =>
        AsStringOrNull(result) ?? result?.ToString() ?? string.Empty;
}
