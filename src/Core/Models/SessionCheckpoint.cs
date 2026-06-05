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
