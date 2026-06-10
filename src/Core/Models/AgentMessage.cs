namespace fuseraft.Core.Models;

/// <summary>
/// A single tool call made by an agent during one turn.
/// </summary>
public record ToolCallRecord(
    /// <summary>Function name (e.g. <c>write_file</c>, <c>shell_run</c>).</summary>
    string Name,
    /// <summary>Compact summary of the most informative argument (e.g. <c>path=src/main.rs</c>).</summary>
    string? ArgsSummary,
    /// <summary>True when the function did not return an error prefix.</summary>
    bool Succeeded,
    /// <summary>Character length of the full serialized args JSON, used to estimate output token cost.</summary>
    int ArgsCharCount = 0);

/// <summary>
/// A single message emitted during an orchestration session.
/// Role is "assistant" for agent turns and "user" for human-in-the-loop injections.
/// </summary>
public record AgentMessage
{
    /// <summary>
    /// Name of the agent, or "Human" for HITL injections.
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// Text content of the response.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// UTC timestamp when the message was received.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Zero-based turn index within the session.
    /// </summary>
    public int TurnIndex { get; init; }

    /// <summary>
    /// "assistant" for agent turns, "user" for HITL injections.
    /// </summary>
    public string Role { get; init; } = "assistant";

    /// <summary>
    /// Token usage and estimated cost for this turn. Null for HITL messages.
    /// For compaction summary messages, <see cref="TokenUsage.CostUsd"/> carries the
    /// cumulative cost of all compacted turns so budget tracking remains accurate.
    /// </summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>
    /// True when this message is an LLM-generated summary that replaces earlier turns.
    /// </summary>
    public bool IsCompactionSummary { get; init; }

    /// <summary>
    /// Tool calls made during this turn, in invocation order.
    /// Null when no tools were called or the orchestrator does not capture tool calls.
    /// </summary>
    public IReadOnlyList<ToolCallRecord>? ToolCalls { get; init; }
}
