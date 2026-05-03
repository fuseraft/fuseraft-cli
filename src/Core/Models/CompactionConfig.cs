namespace fuseraft.Core.Models;

/// <summary>
/// Controls automatic conversation compaction. When the session history exceeds
/// <see cref="TriggerTurnCount"/> messages, older turns are summarised by an LLM and
/// replaced with a single summary message; the most recent <see cref="KeepRecentTurns"/>
/// turns are kept verbatim so agents retain immediate context.
///
/// Compaction fires in two situations:
/// <list type="bullet">
///   <item>Before the stream starts, when resuming a checkpoint that is already over the threshold.</item>
///   <item>Mid-session, after each checkpoint save, once the live history crosses the threshold.</item>
/// </list>
/// </summary>
public record CompactionConfig
{
    /// <summary>
    /// Compact when the message count reaches this value. Default: 50.
    /// </summary>
    public int TriggerTurnCount { get; init; } = 50;

    /// <summary>
    /// Number of most-recent turns to keep verbatim after compaction. Default: 10.
    /// Must be less than <see cref="TriggerTurnCount"/>.
    /// </summary>
    public int KeepRecentTurns { get; init; } = 10;

    /// <summary>
    /// Model used to generate the compaction summary.
    /// Defaults to the first agent's model when null.
    /// </summary>
    public ModelConfig? Model { get; init; }

    /// <summary>
    /// Compaction mode. Default: <c>"llm"</c>.
    /// <list type="bullet">
    ///   <item><c>llm</c> — generate an LLM summary (existing behaviour).</item>
    ///   <item><c>lossless</c> — reconstruct context from durable evidence only; no LLM call.
    ///       Requires an active state machine strategy with evidence contracts. Falls back to
    ///       <c>llm</c> mode when no snapshotter is available.</item>
    ///   <item><c>hybrid</c> — prepend the lossless reconstruction before the LLM summary,
    ///       giving agents both authoritative ground-truth and the narrative context.</item>
    ///   <item><c>window</c> — sliding window: drop the oldest user+assistant pairs until the
    ///       estimated token count is within <see cref="TokenBudget"/>. No LLM call; no summary
    ///       message is injected. Trigger is token-budget based rather than turn-count based,
    ///       so <see cref="TriggerTurnCount"/> is ignored in this mode.</item>
    ///   <item><c>intent</c> — reconstruct context from the intent log instead of an LLM call.
    ///       Produces a deterministic, structured summary of every tool call in the compacted
    ///       range: what was attempted, whether it succeeded or failed, and the target path.
    ///       Requires <see cref="fuseraft.Orchestration.IntentLog"/> to be wired into
    ///       <see cref="fuseraft.Orchestration.ChangeTracker"/> via
    ///       <see cref="ChangeTrackingConfig.IntentLogPath"/>. Falls back to <c>lossless</c>
    ///       mode (then <c>llm</c>) when no intent log is available.</item>
    /// </list>
    /// </summary>
    public string Mode { get; init; } = "llm";

    /// <summary>
    /// Estimated token budget for <c>window</c> mode. Oldest message pairs are dropped
    /// until the total estimated token count (characters ÷ 4) falls within this limit.
    /// Default: 80,000 tokens, which leaves comfortable headroom for a response on every
    /// current major model. Ignored by <c>llm</c>, <c>lossless</c>, and <c>hybrid</c> modes.
    /// </summary>
    public int TokenBudget { get; init; } = 80_000;

    /// <summary>
    /// When <c>true</c>, reasoning excerpts from the compacted turn range are prepended to
    /// the compaction summary. Each excerpt is truncated to approximately 500 tokens so agents
    /// resuming after compaction can see the WHY behind prior decisions, not just the artifacts.
    /// Reads <c>reasoning</c> events from the session's events log. Default: <c>false</c>.
    /// </summary>
    public bool IncludeReasoning { get; init; } = false;

    /// <summary>
    /// When <c>true</c>, a symbol dependency graph derived from the session's changed files is
    /// prepended to the compaction summary (before reasoning excerpts when both are enabled).
    /// Queries <c>SymbolDefinition</c> and <c>SymbolReference</c> nodes from the evidence store
    /// for every file written during the session, giving agents an explicit map of what symbols
    /// were in scope across the compacted turns. Requires an active <c>EvidenceStore</c>.
    /// Default: <c>false</c>.
    /// </summary>
    public bool IncludeSymbolGraph { get; init; } = false;
}
