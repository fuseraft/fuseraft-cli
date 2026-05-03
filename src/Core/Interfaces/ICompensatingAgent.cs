using fuseraft.Core.Models;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Implemented by agents that can undo their side effects when a later step fails.
/// The <see cref="fuseraft.Orchestration.Saga.SagaOrchestrator"/> calls
/// <see cref="CompensateAsync"/> during stack unwind in reverse execution order.
/// Agents without side effects do not need to implement this interface.
/// </summary>
public interface ICompensatingAgent
{
    /// <summary>
    /// Undoes the side effects produced by this agent's execution step.
    /// <paramref name="state"/> is the <see cref="AgentState"/> snapshot captured
    /// when this agent completed, allowing the implementation to read what was done
    /// and reverse it.
    /// </summary>
    Task<AgentState> CompensateAsync(AgentState state, CancellationToken ct);
}
