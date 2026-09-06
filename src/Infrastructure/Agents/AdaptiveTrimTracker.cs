using System.Collections.Concurrent;

namespace fuseraft.Infrastructure.Agents;

/// <summary>
/// Records which agents needed <see cref="AgentMiddlewareBuilder"/>'s adaptive context-trim
/// retry to survive a provider call this cycle. A hit means that agent's context was already
/// too large for a single request — not just approaching a budget — so
/// <c>CompactionCoordinator</c> forces a real compaction before the next turn instead of
/// letting the same oversized history recur.
///
/// <para>
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> because this is written from inside agent
/// execution, which can run concurrently across agents (graph parallel fan-out, map-reduce,
/// scatter-gather) — unlike <c>ContextBudgetManager</c>'s per-turn state, which is only ever
/// touched from the session runner's single-threaded post-turn recording.
/// </para>
/// </summary>
public sealed class AdaptiveTrimTracker
{
    private readonly ConcurrentDictionary<string, byte> _trimmedAgents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marks that <paramref name="agentName"/> needed adaptive trim to complete a call.</summary>
    public void RecordTrim(string agentName) => _trimmedAgents[agentName] = 0;

    /// <summary>
    /// Returns <c>true</c> and clears the flag if <paramref name="agentName"/> needed adaptive
    /// trim since the last check; returns <c>false</c> without side effects otherwise.
    /// </summary>
    public bool ConsumeTrim(string agentName) => _trimmedAgents.TryRemove(agentName, out _);
}
