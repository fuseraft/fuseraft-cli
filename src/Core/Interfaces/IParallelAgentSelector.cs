using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using fuseraft.Orchestration.Parallel;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Implemented by selection strategies that support parallel fan-out.
/// The orchestrator checks for this interface before calling
/// <see cref="IAgentSelector.SelectAsync"/> and routes through the parallel
/// path when a non-null batch is returned.
/// </summary>
public interface IParallelAgentSelector
{
    /// <summary>
    /// Returns a parallel batch when the current history contains a signal that
    /// matches a declared parallel transition, or <c>null</c> when no parallel
    /// transition is ready to fire.
    /// <para>
    /// When a non-null batch is returned the strategy has already advanced its
    /// internal state to the join state — the caller does not need to call
    /// <see cref="IAgentSelector.SelectAsync"/> for this turn.
    /// </para>
    /// </summary>
    Task<ParallelAgentBatch?> TrySelectParallelAsync(
        IReadOnlyList<AIAgent> agents,
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default);
}
