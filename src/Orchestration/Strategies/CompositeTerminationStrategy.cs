using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;

namespace fuseraft.Orchestration.Strategies;

/// <summary>
/// Terminates the conversation when ANY of the provided child conditions signals termination.
/// The orchestrator's own <c>MaxIterations</c> acts as a hard cap independent of child conditions.
/// </summary>
public sealed class CompositeTerminationStrategy(IEnumerable<ITerminationCondition> strategies)
    : ITerminationCondition
{
    public IReadOnlyList<ITerminationCondition> Strategies { get; } = strategies.ToList();

    public async ValueTask<bool> ShouldTerminateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        foreach (var strategy in Strategies)
        {
            if (await strategy.ShouldTerminateAsync(history, cancellationToken))
                return true;
        }

        return false;
    }
}
