using fuseraft.Core.Models;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Provides a point-in-time snapshot of orchestration state so context can be
/// reconstructed losslessly from durable disk artifacts instead of an LLM summary.
///
/// <para>
/// Implemented by selection strategies that maintain explicit, serialisable state
/// (e.g. <c>StateMachineSelectionStrategy</c>). The snapshot is consumed by
/// <see cref="fuseraft.Orchestration.ConversationCompactor"/> when operating in
/// <c>lossless</c> or <c>hybrid</c> compaction mode.
/// </para>
/// </summary>
public interface IContextSnapshotter
{
    /// <summary>
    /// Captures the current orchestration state: state machine position,
    /// all contract evaluations, and the most recent evidence nodes.
    /// </summary>
    Task<ContextSnapshot> SnapshotAsync(CancellationToken ct = default);
}
