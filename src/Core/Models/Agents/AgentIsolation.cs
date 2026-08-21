namespace fuseraft.Core.Models.Agents;

/// <summary>
/// Controls what context an agent receives at each invocation — specifically, whether it sees
/// the shared session transcript other agents have been writing to, or only a synthesized
/// directive plus its own declared <see cref="AgentConfig.Context"/> sources.
/// </summary>
public enum AgentIsolation
{
    /// <summary>
    /// The agent never sees <c>SharedHistory</c>. Its context is built entirely from the
    /// incoming <see cref="AgentDirective"/> (goal/background/constraints synthesized at
    /// handoff time) plus its own declared <see cref="AgentConfig.Context"/> sources, if any.
    /// This is the default: agents do not inherit another agent's reasoning, dead ends, or
    /// tool-call noise unless a <c>Context:</c> source explicitly names it.
    /// </summary>
    Fresh = 0,

    /// <summary>
    /// Legacy/pre-overhaul behavior: <see cref="AgentConfig.Context"/> if declared, otherwise
    /// the windowed shared transcript (<c>SharedHistoryFallback</c>). Required for orchestration
    /// styles that depend on shared visibility to coordinate — e.g. <c>MagenticOrchestrator</c>'s
    /// manager/ledger loop, or simple conversational round-robin/keyword group chats.
    /// </summary>
    Shared = 1,

    /// <summary>
    /// <see cref="Shared"/> behavior plus the synthesized <see cref="AgentDirective"/> layered
    /// on top. For meta-agents that genuinely need the full transcript AND a clear statement of
    /// what to do with it — e.g. a Verifier auditing the session, or a RecoveryAgent diagnosing
    /// a failure.
    /// </summary>
    Fork = 2,
}
