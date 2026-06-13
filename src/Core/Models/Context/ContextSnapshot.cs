namespace fuseraft.Core.Models.Context;

/// <summary>
/// The result of evaluating a single evidence contract at snapshot time.
/// </summary>
public sealed record ContractCheckResult(string Name, bool Passed, string? Error);

/// <summary>
/// Lightweight ADR summary carried in a <see cref="ContextSnapshot"/>.
/// </summary>
public sealed record AdrSummary(string Id, string Title, string Status);

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

    // ── Knowledge layer fields (Gap 9 cross-cutting) ─────────────────────────

    /// <summary>
    /// Active (Accepted-status) ADRs at snapshot time. Populated by
    /// <see cref="fuseraft.Infrastructure.KnowledgeSnapshotEnricher"/> when an ADR registry
    /// is available. Empty when knowledge enrichment is not configured.
    /// </summary>
    public IReadOnlyList<AdrSummary> ActiveAdrs { get; init; } = [];

    /// <summary>
    /// Formatted summary of active long-horizon objectives at snapshot time, or <c>null</c>
    /// when no objectives are active or the objective manager is unavailable.
    /// </summary>
    public string? ObjectiveState { get; init; }

    /// <summary>
    /// Architecture layer violations found at snapshot time. Each entry is a short
    /// human-readable description. Empty when no manifest is configured or no violations exist.
    /// </summary>
    public IReadOnlyList<string> ArchitectureViolations { get; init; } = [];

    /// <summary>
    /// Patterns from the top approved repository memories (by reinforcement count).
    /// Injected at snapshot time so agents resuming after compaction see stable cross-session
    /// knowledge without relying on the pre-turn memory injection path.
    /// </summary>
    public IReadOnlyList<string> TopRepositoryMemories { get; init; } = [];

    /// <summary>
    /// Human-readable summaries of provenance claims that have expired (past their
    /// <c>ExpiresAt</c>). Agents should re-verify any artifact referenced in these warnings
    /// before acting on it.
    /// </summary>
    public IReadOnlyList<string> ExpiredProvenanceWarnings { get; init; } = [];

    // ── State machine failure-tracking fields ────────────────────────────────

    /// <summary>
    /// Active transition failure counter: key = "State::TransitionTo", count = consecutive
    /// failures, error = last validator message. Null when no failure is active.
    /// Populated by <see cref="fuseraft.Orchestration.Strategies.StateMachineSelectionStrategy.SnapshotAsync"/>.
    /// </summary>
    public (string Key, int Count, string LastError)? TransitionFailure { get; init; }

    /// <summary>
    /// Active no-signal counter: state = current state name, count = consecutive turns
    /// without a routing signal. Null when no failure is active.
    /// </summary>
    public (string State, int Count)? NoSignalFailure { get; init; }

    /// <summary>
    /// States entered at least once during the session. Used to detect back-edge signals.
    /// </summary>
    public IReadOnlySet<string> VisitedStates { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-back-edge revisit counts. Key format: "FromState::ToState".
    /// </summary>
    public IReadOnlyDictionary<string, int> BackEdgeVisits { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Transition keys ("State::TransitionTo") for which one-shot recovery logic already fired.
    /// </summary>
    public IReadOnlySet<string> RecoveryActivated { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
