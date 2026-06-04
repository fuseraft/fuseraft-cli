namespace fuseraft.Core.Models;

/// <summary>
/// A factual finding extracted from an agent's tool calls.
///
/// <para>
/// Unlike conversation text (which reflects what the agent <em>said</em>),
/// an <see cref="Observation"/> captures what the agent <em>learned</em> from
/// a tool call — file content, grep results, shell output, etc.
/// Observations survive compaction and inform future summaries regardless of
/// whether the raw tool results are retained in the message history.
/// </para>
/// </summary>
public sealed record Observation
{
    /// <summary>Tool that produced this observation (e.g. <c>read_file</c>, <c>grep_file</c>).</summary>
    public required string Source { get; init; }

    /// <summary>Truncated raw content from the tool result.</summary>
    public required string Evidence { get; init; }

    /// <summary>Concise human-readable summary of the finding.</summary>
    public required string Finding { get; init; }

    /// <summary>Agent that made the observation.</summary>
    public string? AgentName { get; init; }

    /// <summary>Turn index when the observation was made.</summary>
    public int TurnIndex { get; init; }

    /// <summary>
    /// Primary entity this observation concerns — a file path, symbol name, service name, etc.
    /// Derived from the tool call arguments (e.g. the <c>path</c> arg of <c>read_file</c>).
    /// Null when no meaningful entity can be extracted.
    /// </summary>
    public string? Entity { get; init; }

    /// <summary>Estimated confidence (0–1). Higher for read/grep; lower for shell/search.</summary>
    public float Confidence { get; init; } = 0.7f;
}
