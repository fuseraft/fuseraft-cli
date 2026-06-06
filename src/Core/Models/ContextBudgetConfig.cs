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

    /// <summary>
    /// Per-turn input-token ceiling that triggers compaction before the next turn,
    /// independently of the cumulative <see cref="CutoverAt"/> counter. When a
    /// completed turn's input-token count exceeds this value the session history is
    /// compacted before the following agent turn begins.
    ///
    /// <para>
    /// This guards against single-turn explosions — e.g. an agent reading many large
    /// files in one turn — whose individual cost exceeds <see cref="CutoverAt"/> in a
    /// single shot and would leave the next turn carrying an already-bloated history.
    /// Note: this check fires <em>after</em> the expensive turn completes; it prevents
    /// the next turn from inheriting the inflated context, not the current one.
    /// </para>
    ///
    /// <para>
    /// Requires <see cref="OrchestrationConfig.Compaction"/> to be configured.
    /// 0 (default) disables per-turn enforcement.
    /// </para>
    /// </summary>
    public int MaxSingleTurnInputTokens { get; init; } = 0;

    /// <summary>
    /// Maximum estimated tokens that tool-result messages may contribute to the context
    /// sent on any single agent invocation. When the cumulative tool-result token estimate
    /// in the current context exceeds this value, the oldest results beyond the
    /// <see cref="InTurnToolWindow"/> are replaced with one-line tombstones before the
    /// next LLM call — keeping the model aware of what was done without replaying raw content.
    ///
    /// <para>
    /// Applies per-invocation (not per-session). The full tool results remain in the
    /// shared history for compaction and audit purposes; only the view sent to the model
    /// is trimmed.
    /// </para>
    ///
    /// <para>0 (default) disables the tool-result window.</para>
    /// </summary>
    public int MaxToolResultTokens { get; init; } = 0;

    /// <summary>
    /// Number of most-recent tool result messages to always retain verbatim when the
    /// <see cref="MaxToolResultTokens"/> window is exceeded. Older results beyond this
    /// count are replaced with tombstones.
    /// Defaults to 20.
    /// </summary>
    public int InTurnToolWindow { get; init; } = 20;
}
