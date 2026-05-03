namespace fuseraft.Core.Models;

/// <summary>
/// The result of evaluating a single evidence contract at snapshot time.
/// </summary>
public sealed record ContractCheckResult(string Name, bool Passed, string? Error);

/// <summary>
/// A point-in-time snapshot of the orchestration state used for lossless context
/// reconstruction. All fields are derived from durable disk artifacts so the snapshot
/// carries no hallucination risk, unlike an LLM-generated summary.
/// </summary>
public sealed record ContextSnapshot
{
    /// <summary>
    /// Name of the state the machine is currently in.
    /// Null when no state machine strategy is active.
    /// </summary>
    public string? CurrentStateName { get; init; }

    /// <summary>
    /// Evaluation result for every contract known to the engine at snapshot time.
    /// An empty list means no contracts were declared.
    /// </summary>
    public IReadOnlyList<ContractCheckResult> ContractResults { get; init; } = [];

    /// <summary>
    /// Most recent evidence nodes from the evidence store, ordered newest first.
    /// Empty when no evidence store is configured.
    /// </summary>
    public IReadOnlyList<EvidenceNode> RecentEvidence { get; init; } = [];

    /// <summary>Session ID active when the snapshot was taken.</summary>
    public string? SessionId { get; init; }

    /// <summary>UTC time the snapshot was taken.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
