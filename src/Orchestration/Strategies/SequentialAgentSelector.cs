using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;

namespace fuseraft.Orchestration.Strategies;

/// <summary>Round-robin sequential agent selector.</summary>
internal sealed class SequentialAgentSelector : IAgentSelector
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
