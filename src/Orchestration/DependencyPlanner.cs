using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

/// <summary>
/// Optional scheduling layer that enforces <c>Produces</c>/<c>Requires</c> token dependencies
/// declared on <see cref="AgentConfig"/> entries.
///
/// <para>
/// Construction validates the dependency graph (cycle detection via topological sort) and throws
/// <see cref="InvalidOperationException"/> when a cycle is detected. The planner is activated
/// only when at least one agent declares <c>Produces</c> or <c>Requires</c>.
/// </para>
///
/// <para>
/// During a session:
/// <list type="number">
///   <item>Call <see cref="CanExecute"/> to check whether an agent's prerequisites are satisfied.</item>
///   <item>Call <see cref="Fulfill"/> after an agent turn completes to add its produced tokens to the fulfilled set.</item>
///   <item>Read <see cref="FulfilledTokens"/> from validators or context assembly for observable state.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DependencyPlanner
{
    private readonly IReadOnlyList<AgentConfig> _agents;
    private readonly HashSet<string> _fulfilled = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// Grouped layers of agent names that can execute in parallel within each layer.
    /// Agents in layer 0 have no requirements; agents in layer N require at least one
    /// token produced by layer N-1 or earlier.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> ExecutionLayers { get; }

    /// <summary>
    /// Flat topological execution order derived from <see cref="ExecutionLayers"/>.
    /// </summary>
    public IReadOnlyList<string> TopologicalOrder { get; }

    /// <summary>
    /// True when at least one agent declares <c>Produces</c> or <c>Requires</c>.
    /// When false the planner is a no-op and should not affect routing.
    /// </summary>
    public bool HasDependencies { get; }

    /// <summary>
    /// The current set of fulfilled tokens, updated by <see cref="Fulfill"/>.
    /// </summary>
    public IReadOnlySet<string> FulfilledTokens
    {
        get { lock (_lock) return _fulfilled.ToHashSet(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Fired whenever a new token is added to the fulfilled set.
    /// </summary>
    public event Action<string>? TokenFulfilled;

    public DependencyPlanner(IReadOnlyList<AgentConfig> agents)
    {
        _agents = agents;

        HasDependencies = agents.Any(a => a.Produces.Count > 0 || a.Requires.Count > 0);

        (ExecutionLayers, TopologicalOrder) = HasDependencies
            ? BuildAndValidate(agents)
            : ([], []);
    }

    /// <summary>
    /// Returns true when all <c>Requires</c> tokens for <paramref name="agentName"/> are
    /// present in the fulfilled set. Always returns true for agents with no <c>Requires</c>.
    /// </summary>
    public bool CanExecute(string agentName)
    {
        var cfg = _agents.FirstOrDefault(a =>
            string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));
        if (cfg is null || cfg.Requires.Count == 0) return true;

        lock (_lock)
            return cfg.Requires.All(r => _fulfilled.Contains(r));
    }

    /// <summary>
    /// Returns agents whose <c>Requires</c> are fully satisfied by the current fulfilled set.
    /// </summary>
    public IReadOnlyList<AgentConfig> GetEligible()
    {
        lock (_lock)
            return _agents.Where(a => a.Requires.All(r => _fulfilled.Contains(r))).ToList();
    }

    /// <summary>
    /// Marks all <c>Produces</c> tokens declared by <paramref name="agentName"/> as fulfilled.
    /// </summary>
    public void Fulfill(string agentName)
    {
        var cfg = _agents.FirstOrDefault(a =>
            string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));
        if (cfg is null || cfg.Produces.Count == 0) return;

        foreach (var token in cfg.Produces)
        {
            bool added;
            lock (_lock) added = _fulfilled.Add(token);
            if (added) TokenFulfilled?.Invoke(token);
        }
    }

    /// <summary>
    /// Returns a human-readable list of unmet <c>Requires</c> tokens for <paramref name="agentName"/>.
    /// Returns an empty list when all prerequisites are satisfied.
    /// </summary>
    public IReadOnlyList<string> GetUnmetRequirements(string agentName)
    {
        var cfg = _agents.FirstOrDefault(a =>
            string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));
        if (cfg is null || cfg.Requires.Count == 0) return [];

        lock (_lock)
            return cfg.Requires.Where(r => !_fulfilled.Contains(r)).ToList();
    }

    // Builds the execution layers via Kahn's topological sort. Throws on cycles.
    private static (IReadOnlyList<IReadOnlyList<string>> Layers, IReadOnlyList<string> Order)
        BuildAndValidate(IReadOnlyList<AgentConfig> agents)
    {
        // Map each token to the set of agent names that produce it.
        var producerMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents)
        {
            foreach (var token in agent.Produces)
            {
                if (!producerMap.TryGetValue(token, out var list))
                    producerMap[token] = list = [];
                list.Add(agent.Name);
            }
        }

        // Build adjacency list: producer → consumer (edge: producer must run before consumer).
        var inDegree    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var adjacency   = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var agent in agents)
        {
            inDegree.TryAdd(agent.Name, 0);
            adjacency.TryAdd(agent.Name, []);
        }

        foreach (var consumer in agents)
        {
            foreach (var req in consumer.Requires)
            {
                if (!producerMap.TryGetValue(req, out var producers)) continue;

                foreach (var producerName in producers)
                {
                    if (string.Equals(producerName, consumer.Name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Agent '{consumer.Name}' both produces and requires token '{req}' — self-dependency is not allowed.");

                    adjacency[producerName].Add(consumer.Name);
                    inDegree[consumer.Name]++;
                }
            }
        }

        // Kahn's algorithm — processes agents in topological layers.
        var queue  = new Queue<string>();
        var layers = new List<IReadOnlyList<string>>();
        var order  = new List<string>();

        var currentInDegree = new Dictionary<string, int>(inDegree, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in currentInDegree.Where(kv => kv.Value == 0))
            queue.Enqueue(kv.Key);

        while (queue.Count > 0)
        {
            // All agents currently in the queue form one parallel layer.
            var layer = new List<string>();
            int count = queue.Count;
            for (int i = 0; i < count; i++)
            {
                var node = queue.Dequeue();
                layer.Add(node);
                order.Add(node);

                foreach (var neighbor in adjacency[node])
                {
                    if (--currentInDegree[neighbor] == 0)
                        queue.Enqueue(neighbor);
                }
            }
            layers.Add(layer);
        }

        if (order.Count != agents.Count)
        {
            // Find the cycle participants for the error message.
            var inCycle = agents
                .Select(a => a.Name)
                .Except(order, StringComparer.OrdinalIgnoreCase)
                .ToList();
            throw new InvalidOperationException(
                $"Dependency cycle detected among agents: {string.Join(", ", inCycle.Select(n => $"'{n}'"))}. " +
                "Verify that no agent's Requires token is only produced by agents that depend on it (directly or transitively).");
        }

        return (layers, order);
    }
}
