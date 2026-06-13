namespace fuseraft.Core.Models.Config;

/// <summary>
/// Classifies the root cause of a routing validator failure so the orchestrator can
/// apply a targeted response rather than always escalating after a fixed count.
/// </summary>
public enum FailureType
{
    /// <summary>
    /// A required artifact (brief, test report, written file) is absent from disk.
    /// The agent needs specific instructions to create the missing artifact.
    /// </summary>
    MissingEvidence,

    /// <summary>
    /// The agent emitted a handoff keyword without completing its prerequisites
    /// (e.g. no <c>write_file</c> call, no passing shell command).
    /// The agent needs to be routed back with a correction.
    /// </summary>
    InvalidTransition,

    /// <summary>
    /// The evidence on disk is internally inconsistent — for example a test report
    /// claiming PASS for commands that were never run, or a change log that contradicts
    /// what the agent claims in prose. Requires a deeper audit before proceeding.
    /// </summary>
    ConflictingEvidence,

    /// <summary>
    /// The agent re-emitted the handoff keyword without calling any tools, meaning it
    /// produced no observable work. Indicates a stuck agent that cannot self-correct
    /// through normal retries.
    /// </summary>
    NoProgress,
}

/// <summary>
/// Determines how the orchestrator responds to a classified failure.
/// </summary>
public enum FailureAction
{
    /// <summary>
    /// Inject targeted instructions explaining exactly what artifact or action is missing,
    /// then re-invoke the source agent. Best for <see cref="FailureType.MissingEvidence"/>.
    /// </summary>
    Reinstruct,

    /// <summary>
    /// Immediately activate the route's <c>RecoveryAgent</c> (if configured) without
    /// waiting for the usual consecutive-failure threshold. Falls back to
    /// <see cref="Reinstruct"/> when no recovery agent is declared.
    /// </summary>
    ActivateRecovery,

    /// <summary>
    /// Immediately escalate to human-in-the-loop by throwing
    /// <see cref="Core.Exceptions.ValidatorStuckException"/>, bypassing the normal threshold.
    /// Use for failure types that indicate systemic problems (e.g. prompt injection).
    /// </summary>
    EscalateToHuman,

    /// <summary>
    /// Continue injecting corrections until <see cref="FailureTypeConfig.Threshold"/>
    /// consecutive failures are reached, then escalate to HITL via
    /// <see cref="Core.Exceptions.ValidatorStuckException"/>.
    /// </summary>
    Abort,
}

/// <summary>
/// Per-failure-type handling policy.
/// </summary>
public record FailureTypeConfig
{
    /// <summary>
    /// The action to take when this failure type is detected.
    /// </summary>
    public FailureAction Action { get; init; } = FailureAction.Abort;

    /// <summary>
    /// Number of consecutive failures of this type before escalating (used by
    /// <see cref="FailureAction.Abort"/>; ignored by immediate-escalation actions).
    /// Defaults to 3.
    /// </summary>
    public int Threshold { get; init; } = 3;
}

/// <summary>
/// Maps each <see cref="FailureType"/> to a handling policy. When omitted from the
/// config the defaults below mirror the legacy uniform-threshold behaviour while
/// providing better injected messages.
/// </summary>
public record FailureHandlingConfig
{
    /// <summary>
    /// Policy for <see cref="FailureType.MissingEvidence"/> (required artifact absent).
    /// Default: inject targeted reinstructions, escalate after 3 consecutive failures.
    /// </summary>
    public FailureTypeConfig MissingEvidence { get; init; } =
        new() { Action = FailureAction.Reinstruct, Threshold = 3 };

    /// <summary>
    /// Policy for <see cref="FailureType.InvalidTransition"/> (prerequisite incomplete).
    /// Default: reinstructions with a correction message, escalate after 3 failures.
    /// </summary>
    public FailureTypeConfig InvalidTransition { get; init; } =
        new() { Action = FailureAction.Reinstruct, Threshold = 3 };

    /// <summary>
    /// Policy for <see cref="FailureType.ConflictingEvidence"/> (evidence inconsistency).
    /// Default: reinstructions with an audit-focused correction, escalate after 2 consecutive
    /// failures (faster escalation because conflicting evidence often indicates a stuck or
    /// hallucinating agent).
    /// </summary>
    public FailureTypeConfig ConflictingEvidence { get; init; } =
        new() { Action = FailureAction.Reinstruct, Threshold = 2 };

    /// <summary>
    /// Policy for <see cref="FailureType.NoProgress"/> (no tool calls between retries).
    /// Default: escalate to HITL after 3 consecutive no-op turns. Override to 2 for
    /// faster escalation on workflows where agents are expected to always use tools.
    /// </summary>
    public FailureTypeConfig NoProgress { get; init; } =
        new() { Action = FailureAction.Abort, Threshold = 3 };

    /// <summary>
    /// Maximum consecutive turns the active-state agent may run without emitting any
    /// routing signal before the orchestrator escalates to HITL. Unlike
    /// <see cref="MaxConsecutiveContractFailures"/> (which counts failures when a signal
    /// IS detected but a contract blocks it), this counter fires when the agent produces
    /// no matching signal at all — the "silent stuck" case.
    ///
    /// <para>
    /// The counter is stored in strategy state, not in history, so it survives
    /// compaction cycles. It resets whenever the agent emits a valid signal (even if
    /// the subsequent contract check fails) or when a transition succeeds.
    /// 0 (default) disables this guard.
    /// </para>
    /// </summary>
    public int MaxConsecutiveTurnsWithoutSignal { get; init; } = 0;

    /// <summary>
    /// Hard backstop applied across all failure types and all transitions. When any
    /// single state-to-state transition accumulates this many consecutive contract
    /// failures — regardless of the per-type <see cref="FailureTypeConfig.Action"/> —
    /// the orchestrator escalates to HITL via <see cref="Core.Exceptions.ValidatorStuckException"/>.
    ///
    /// <para>
    /// This prevents a <see cref="FailureAction.Reinstruct"/> policy from looping forever
    /// when a contract cannot be satisfied: the configured type threshold continues to
    /// control when reinstructions stop and the type-specific escalation fires, but this
    /// global ceiling ensures no transition fails more than N times total regardless of
    /// the type policy. 0 (default) disables the global backstop.
    /// </para>
    /// </summary>
    public int MaxConsecutiveContractFailures { get; init; } = 0;

    /// <summary>Returns the <see cref="FailureTypeConfig"/> for <paramref name="type"/>.</summary>
    public FailureTypeConfig GetConfig(FailureType type) => type switch
    {
        FailureType.MissingEvidence     => MissingEvidence,
        FailureType.InvalidTransition   => InvalidTransition,
        FailureType.ConflictingEvidence => ConflictingEvidence,
        FailureType.NoProgress          => NoProgress,
        _ => new FailureTypeConfig()
    };
}
