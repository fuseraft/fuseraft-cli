using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Workflow;

/// <summary>
/// Produces immutable <see cref="AgentState"/> snapshots as data moves between agents.
/// Every call to <see cref="Advance"/> returns a brand-new snapshot; the original is never modified.
/// </summary>
public static class StateHandoff
{
    /// <summary>
    /// Creates the next versioned snapshot by merging <paramref name="mutations"/> onto the
    /// current snapshot's data. Keys present in <paramref name="mutations"/> overwrite existing
    /// values; keys absent in <paramref name="mutations"/> are carried forward unchanged.
    /// </summary>
    /// <param name="current">The snapshot produced by the previous agent turn.</param>
    /// <param name="nextAgent">Name of the agent that will receive the new snapshot.</param>
    /// <param name="mutations">Key-value pairs to merge into the next snapshot's data.</param>
    /// <returns>A new <see cref="AgentState"/> with <c>Version + 1</c> and merged data.</returns>
    public static AgentState Advance(
        AgentState current,
        string nextAgent,
        IReadOnlyDictionary<string, object?> mutations)
    {
        var merged = new Dictionary<string, object?>(current.Data);

        foreach (var (key, value) in mutations)
            merged[key] = value;

        return new AgentState
        {
            Version   = current.Version + 1,
            CreatedBy = nextAgent,
            CreatedAt = DateTimeOffset.UtcNow,
            Data      = merged
        };
    }

    /// <summary>
    /// Convenience overload for advancing without any data mutations (state version bump only).
    /// </summary>
    public static AgentState Advance(AgentState current, string nextAgent) =>
        Advance(current, nextAgent, new Dictionary<string, object?>());
}
