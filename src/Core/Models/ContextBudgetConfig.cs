namespace fuseraft.Core.Models;

/// <summary>
/// Controls per-agent context budget enforcement. Tracks cumulative input tokens
/// per agent across turns and reacts when thresholds are crossed — warning before
/// context rot sets in, then triggering compaction to keep the session alive
/// indefinitely rather than halting with a hard error.
///
/// <para>
/// Unlike <see cref="OrchestrationConfig.MaxTotalTokens"/>, which counts combined
/// input + output tokens across all agents and terminates the session on breach,
/// <c>ContextBudget</c> counts input tokens per agent independently and responds
/// with compaction rather than termination.
/// </para>
///
/// <para>
/// Counters reset after each compaction cycle so a session with compaction enabled
/// can run indefinitely: each new context window starts with a fresh budget.
/// </para>
/// </summary>
public record ContextBudgetConfig
{
    /// <summary>
    /// Cumulative input-token threshold per agent that triggers a warning.
    /// When any agent's accumulated input tokens since the last compaction reach
    /// this value, a warning is printed and a <c>context_budget_warn</c> event is
    /// emitted. The warning fires once per agent per compaction cycle.
    /// 0 (default) disables the warning.
    /// </summary>
    public int WarnAt { get; init; } = 0;

    /// <summary>
    /// Cumulative input-token threshold per agent that triggers automatic compaction.
    /// When any agent's accumulated input tokens since the last compaction reach
    /// this value, the session history is compacted before the next agent turn.
    /// The context budget counters reset after compaction so the next window starts
    /// clean. 0 (default) disables automatic cutover.
    ///
    /// <para>
    /// Requires <see cref="OrchestrationConfig.Compaction"/> to be configured —
    /// compaction cannot fire without a compactor.
    /// </para>
    /// </summary>
    public int CutoverAt { get; init; } = 0;
}
