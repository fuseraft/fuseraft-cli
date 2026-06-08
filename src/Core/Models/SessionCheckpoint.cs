namespace fuseraft.Core.Models;

/// <summary>
/// Persisted state of an orchestration session, written to disk after every agent turn
/// so that an interrupted session can be resumed.
/// </summary>
public record SessionCheckpoint
{
    /// <summary>
    /// Short unique identifier (8 hex chars).
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// The original task string submitted by the user.
    /// </summary>
    public required string Task { get; init; }

    /// <summary>
    /// Path to the orchestration config used for this session.
    /// </summary>
    public required string ConfigPath { get; init; }

    /// <summary>
    /// Absolute working directory at session start. Used by the session index to
    /// group and filter sessions by project. Null for sessions created before this
    /// field was introduced (backward compatible).
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// All agent messages produced so far, in order.
    /// </summary>
    public List<AgentMessage> Messages { get; init; } = [];

    /// <summary>
    /// UTC timestamp when the session was first created.
    /// </summary>
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp of the most recent update.
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True once the session has run to completion (no longer resumable).
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// Executor ID to resume from after a compaction or mid-session restart.
    /// Set by the orchestration layer immediately before compaction fires so that
    /// the correct agent is selected even when the retained history contains no
    /// handoff keyword.  Null means "let the orchestrator infer from history."
    /// </summary>
    public string? ResumeExecutorId { get; set; }

    /// <summary>
    /// State machine state name at the moment compaction fired (e.g. "Testing").
    /// Persisted so the orchestrator can restore the correct state when the next
    /// StreamAsync call creates a fresh StateMachineSelectionStrategy instance.
    /// Null for keyword-routing sessions or when no compaction has occurred.
    /// </summary>
    public string? CurrentStateName { get; set; }

    /// <summary>
    /// Magentic orchestration loop state, persisted so a paused session can resume
    /// mid-plan without re-running fact-gathering. Null for non-Magentic sessions.
    /// </summary>
    public MagenticCheckpointState? MagenticState { get; set; }

    /// <summary>
    /// Structured goal, constraints, and active file targets for this session.
    /// Populated from the raw <see cref="Task"/> string at session start and updated
    /// incrementally as agents write files. Null for sessions started before this field
    /// was introduced (backward compatible — callers fall back to <see cref="Task"/>).
    /// </summary>
    public TaskModel? StructuredTask { get; set; }

    /// <summary>
    /// Ordered list of immutable <see cref="AgentState"/> snapshots produced during the
    /// session, one per successful agent handoff. The first entry is the version-0 seed
    /// created at session start; each subsequent entry is produced by
    /// <see cref="fuseraft.Orchestration.Workflow.StateHandoff.Advance"/>. Null for sessions
    /// that use orchestrators other than <c>GraphOrchestrator</c>.
    /// </summary>
    public IReadOnlyList<AgentState>? StateHistory { get; set; }

    /// <summary>
    /// Failure-tracking counters for the state machine, captured at compaction time and
    /// restored on the next <c>StreamAsync</c> call. Null for non-state-machine sessions
    /// or sessions where no compaction has occurred.
    /// </summary>
    public StateMachineCheckpointState? StateMachineState { get; set; }
}

/// <summary>
/// Snapshot of the MagenticOrchestrator's loop counters taken before each checkpoint save.
/// Allows the orchestrator to resume at the correct round without replaying the planning phase.
/// </summary>
public record MagenticCheckpointState
{
    /// <summary>The current plan text produced by the manager.</summary>
    public string? CurrentPlan { get; init; }

    /// <summary>
    /// Structured step list parsed from <see cref="CurrentPlan"/>.
    /// Null when the manager did not emit a JSON step block, or for sessions started
    /// before this field was introduced (backward compatible).
    /// </summary>
    public PlanStep[]? CurrentPlanSteps { get; init; }

    /// <summary>Inner-loop round index at checkpoint time.</summary>
    public int RoundIndex { get; init; }

    /// <summary>Consecutive stall count at checkpoint time.</summary>
    public int StallCount { get; init; }

    /// <summary>Replan cycle count at checkpoint time.</summary>
    public int ResetCount { get; init; }

    /// <summary>
    /// True when the checkpoint was taken while waiting for HITL plan review.
    /// The orchestrator re-emits the plan review prompt on resume.
    /// </summary>
    public bool AwaitingPlanReview { get; init; }
}

/// <summary>
/// Serialisable snapshot of the <c>StateMachineSelectionStrategy</c> failure-tracking
/// counters captured at compaction time. Restored at the start of the next
/// <c>StreamAsync</c> call so <see cref="SessionCheckpoint.StateMachineState"/> and
/// <see cref="FailureHandlingConfig.MaxConsecutiveContractFailures"/> survive compaction.
/// </summary>
public record StateMachineCheckpointState
{
    /// <summary>Key of the active transition failure ("State::TransitionTo"). Null when no failure is active.</summary>
    public string? TransitionFailureKey { get; init; }

    /// <summary>Consecutive failure count for the active transition. Meaningful only when <see cref="TransitionFailureKey"/> is non-null.</summary>
    public int TransitionFailureCount { get; init; }

    /// <summary>Last validator error message for the active transition failure. May be empty.</summary>
    public string? TransitionFailureError { get; init; }

    /// <summary>State name of the active no-signal failure. Null when no no-signal failure is active.</summary>
    public string? NoSignalFailureState { get; init; }

    /// <summary>Consecutive turns without a routing signal. Meaningful only when <see cref="NoSignalFailureState"/> is non-null.</summary>
    public int NoSignalFailureCount { get; init; }

    /// <summary>States entered at least once during the session.</summary>
    public List<string> VisitedStates { get; init; } = [];

    /// <summary>Per-back-edge revisit counts. Key format: "FromState::ToState".</summary>
    public Dictionary<string, int> BackEdgeVisits { get; init; } = [];

    /// <summary>Transition keys for which one-shot recovery logic already fired.</summary>
    public List<string> RecoveryActivated { get; init; } = [];
}
