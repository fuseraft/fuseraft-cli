using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Selects the next agent to run in a multi-agent orchestration loop.
/// Called after each agent turn to determine which agent should respond next.
/// </summary>
public interface IAgentSelector
{
    /// <summary>
    /// Returns the next agent to run, or null to end the orchestration.
    /// </summary>
    Task<AIAgent?> SelectAsync(
        IReadOnlyList<AIAgent> agents,
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default);
}
