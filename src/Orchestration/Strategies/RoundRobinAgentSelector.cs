using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;

namespace fuseraft.Orchestration.Strategies;

/// <summary>
/// Cycles through agents indefinitely in round-robin order. Wraps back to the first
/// agent after the last — selection only ends when a termination strategy fires or
/// the hard iteration cap is reached.
/// </summary>
internal sealed class RoundRobinAgentSelector : IAgentSelector
{
    private int _index = -1;

    public Task<AIAgent?> SelectAsync(
        IReadOnlyList<AIAgent> agents,
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        if (agents.Count == 0) return Task.FromResult<AIAgent?>(null);
        _index = (_index + 1) % agents.Count;
        return Task.FromResult<AIAgent?>(agents[_index]);
    }
}
