using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;

namespace fuseraft.Orchestration.Strategies;

/// <summary>
/// Advances through agents in declaration order exactly once. Returns <c>null</c> after
/// the last agent, which causes <c>AgentOrchestrator</c> to break its loop — the
/// termination strategy controls whether that null is ever reached (e.g. a
/// <c>maxiterations</c> cap set to the number of agents gives a single pass).
/// For indefinite cycling use <see cref="RoundRobinAgentSelector"/>.
/// </summary>
internal sealed class SequentialAgentSelector : IAgentSelector
{
    private int _index = -1;

    public Task<AIAgent?> SelectAsync(
        IReadOnlyList<AIAgent> agents,
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        if (agents.Count == 0) return Task.FromResult<AIAgent?>(null);
        _index++;
        if (_index >= agents.Count) return Task.FromResult<AIAgent?>(null);
        return Task.FromResult<AIAgent?>(agents[_index]);
    }
}
