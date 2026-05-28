namespace fuseraft.Core.Models;

/// <summary>
/// Controls how conversation history is filtered before being passed to an agent.
///
/// By default every agent receives the full accumulated <c>ChatMessage</c> history,
/// which includes tool-call frames and tool-result messages from all prior turns.
/// In a long multi-agent session this history can grow to hundreds of thousands of
/// tokens — most of it irrelevant to late-stage agents such as a code reviewer that
/// only needs the final handoff text and its own spot-checks.
///
/// <c>ContextWindow</c> lets each agent declare a lighter view of the conversation.
/// Filters are applied in order: <see cref="TextOnly"/> / <see cref="ExcludeAgents"/>
/// first, then <see cref="MaxTailMessages"/>. The shared history is never mutated —
/// only the slice passed to that agent's <c>RunAsync</c> is affected.
/// </summary>
public sealed record ContextWindowConfig
{
    /// <summary>
    /// When <c>true</c>, strips all tool-call frames (assistant messages that contain only
    /// a function-call request and no text) and all tool-result messages from the history
    /// slice before it is passed to this agent.
    ///
    /// Text-bearing assistant messages and all user messages are kept.
    /// The agent's own tool calls within the current turn are unaffected because those
    /// messages are appended to the shared history <em>after</em> the context is built.
    ///
    /// This is the primary lever for reducing context size. A downstream agent such as a
    /// Reviewer that independently re-reads files and re-runs shell commands gains nothing
    /// from seeing hundreds of tool-result messages produced by earlier agents.
    ///
    /// Default: <c>false</c>.
    /// </summary>
    public bool TextOnly { get; init; }

    /// <summary>
    /// Names of agents whose messages should be excluded from this agent's context.
    /// Both text-bearing assistant messages and tool-call frames authored by the listed
    /// agents are stripped.
    ///
    /// When this list is non-empty, tool-result messages (<c>ChatRole.Tool</c>) are also
    /// stripped regardless of <see cref="TextOnly"/>, because tool results are not attributed
    /// to a specific agent and leaving them without their corresponding call requests would
    /// produce a malformed context.
    ///
    /// Default: empty (no agent exclusions).
    /// </summary>
    public List<string> ExcludeAgents { get; init; } = [];

    /// <summary>
    /// When greater than zero, retains only the last <em>N</em> messages from the
    /// (possibly already filtered) history slice. Applied after <see cref="TextOnly"/>
    /// and <see cref="ExcludeAgents"/>.
    ///
    /// Useful for very long sessions where accumulated agent text still grows beyond
    /// what a terminal agent needs.
    ///
    /// Default: <c>0</c> (no limit).
    /// </summary>
    public int MaxTailMessages { get; init; }

    /// <summary>
    /// Fraction of <see cref="MaxTailMessages"/> at which a <c>context_cap_warning</c>
    /// event is emitted before the next agent turn.  For example, <c>0.4</c> warns when
    /// the filtered message count exceeds 40% of <see cref="MaxTailMessages"/>.
    ///
    /// This gives the orchestrator an early signal to trigger inline compaction rather
    /// than waiting for the hard <see cref="MaxTailMessages"/> limit to be reached.
    ///
    /// Only meaningful when <see cref="MaxTailMessages"/> is also set.
    /// Default: <c>0</c> (disabled).
    /// </summary>
    public double ContextCapFraction { get; init; }

    /// <summary>
    /// When greater than zero, keeps only messages from the last <em>N agent turns</em>
    /// (where each turn ends with an assistant reply). Applied after
    /// <see cref="TextOnly"/> / <see cref="ExcludeAgents"/> and before
    /// <see cref="MaxTailMessages"/>.
    ///
    /// <para>
    /// Unlike <see cref="MaxTailMessages"/>, which is a raw message count,
    /// <c>MaxTurnAge</c> is semantic: it counts assistant turns backward from the end of
    /// history and discards everything before the cut-point. This prevents irrelevant
    /// early-session context (from different phases or different agents) from inflating
    /// input tokens for agents that only need to understand the last few turns.
    /// </para>
    ///
    /// Default: <c>0</c> (disabled).
    /// </summary>
    public int MaxTurnAge { get; init; }

    /// <summary>
    /// Maximum characters to replay from a single tool-result (<c>ChatRole.Tool</c>) message
    /// in the history slice passed to this agent. When a tool result exceeds this limit the
    /// result string is truncated and a suffix noting the omitted character count is appended.
    ///
    /// <para>
    /// This prevents large tool outputs — e.g. a <c>read_file</c> on a 200 KB file — from
    /// being replayed verbatim in every subsequent agent turn, compounding context growth.
    /// Unlike <see cref="TextOnly"/> (which drops tool messages entirely), this option keeps
    /// the tool result visible but bounded.
    /// </para>
    ///
    /// Default: <c>0</c> (no truncation).
    /// </summary>
    public int MaxToolResultChars { get; init; }
}
