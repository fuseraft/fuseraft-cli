namespace fuseraft.Core.Models;

/// <summary>
/// Final result returned by <see cref="fuseraft.Core.Interfaces.IOrchestrator.RunAsync"/>.
/// </summary>
public record OrchestrationResult
{
    /// <summary>
    /// Short unique identifier for this session (8 hex chars).
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// True when the orchestration completed without an unhandled error.
    /// </summary>
    public bool Succeeded { get; init; }

    /// <summary>
    /// All agent messages produced during the session, in order.
    /// </summary>
    public IReadOnlyList<AgentMessage> Messages { get; init; } = [];

    /// <summary>
    /// Human-readable reason the session ended ("Completed", "Cancelled", "Error").
    /// </summary>
    public string? TerminationReason { get; init; }

    /// <summary>
    /// Wall-clock time from first message to last.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Error message when <see cref="Succeeded"/> is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Total number of agent turns.
    /// </summary>
    public int TotalTurns => Messages.Count;
}
