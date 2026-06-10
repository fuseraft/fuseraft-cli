using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

/// <summary>
/// Captures all mutable state scoped to a single <see cref="AgentOrchestrator.StreamAsync"/>
/// invocation. Isolating per-session state here prevents cross-session field mutation when
/// the orchestrator is reused across sequential calls.
/// </summary>
internal sealed class OrchestrationSession
{
    /// <summary>Session correlation ID stamped on all governance and telemetry events.</summary>
    public string SessionId { get; }

    /// <summary>Shared conversation history written by all agents in this session.</summary>
    public List<ChatMessage> History { get; } = [];

    /// <summary>
    /// Active selection strategy cast to <see cref="IContextSnapshotter"/>, or null
    /// when the current strategy does not support snapshotting.
    /// Set once at session startup after strategy creation.
    /// </summary>
    public IContextSnapshotter? Snapshotter { get; set; }

    /// <summary>
    /// State-machine state name to restore on first turn, consumed by strategy
    /// initialisation. Captured from the orchestrator's pre-session setter on construction.
    /// </summary>
    public string? ResumeStateName { get; }

    /// <summary>
    /// Failure-counter snapshot to restore on first turn, consumed by strategy
    /// initialisation. Captured from the orchestrator's pre-session setter on construction.
    /// </summary>
    public StateMachineCheckpointState? ResumeSnapshot { get; }

    public OrchestrationSession(
        string sessionId,
        string? resumeStateName,
        StateMachineCheckpointState? resumeSnapshot)
    {
        SessionId = sessionId;
        ResumeStateName = resumeStateName;
        ResumeSnapshot = resumeSnapshot;
    }
}
