namespace fuseraft.Core.Models.Config;

/// <summary>
/// Configures the self-verification meta-agent that audits the evidence graph for
/// inconsistencies, challenges unverified claims, and requests re-execution when
/// agent output cannot be reconciled with recorded evidence.
/// </summary>
public record VerifierConfig
{
    /// <summary>
    /// Name of the verifier agent in the <c>Agents</c> list. Must match exactly.
    /// </summary>
    public string AgentName { get; init; } = string.Empty;

    /// <summary>
    /// Run the verifier every N agent turns (0-based turn index).
    /// 0 disables periodic verification; the verifier only fires on suspicious transitions.
    /// </summary>
    public int EveryNTurns { get; init; } = 0;

    /// <summary>
    /// When <c>true</c>, the state machine automatically selects the verifier on the turn
    /// immediately following a <c>ConflictingEvidence</c> or <c>NoProgress</c> contract
    /// failure, giving it one turn to audit before re-invoking the primary agent.
    /// Default: <c>true</c>.
    /// </summary>
    public bool TriggerOnSuspiciousTransition { get; init; } = true;

    /// <summary>
    /// Case-insensitive keyword the verifier includes in its output when an inconsistency
    /// is detected. When found, the orchestrator injects the verifier's full response as a
    /// user-visible correction message so the next agent has ground-truth context.
    /// Default: <c>"INCONSISTENCY"</c>.
    /// </summary>
    public string FindingsKeyword { get; init; } = "INCONSISTENCY";
}
