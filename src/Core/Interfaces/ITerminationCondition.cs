using Microsoft.Extensions.AI;

namespace fuseraft.Core.Interfaces;

/// <summary>
/// Determines whether a multi-agent orchestration should terminate after each agent turn.
/// </summary>
public interface ITerminationCondition
{
    /// <summary>
    /// Returns true when the orchestration should stop.
    /// </summary>
    ValueTask<bool> ShouldTerminateAsync(
        IList<ChatMessage> history,
        CancellationToken cancellationToken = default);
}
