using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;

namespace fuseraft.Orchestration.Strategies;

/// <summary>Termination condition that never terminates (used for maxiterations-only configs).</summary>
internal sealed class NeverTerminationCondition : ITerminationCondition
{
    public static readonly NeverTerminationCondition Instance = new();

    public ValueTask<bool> ShouldTerminateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);
}
