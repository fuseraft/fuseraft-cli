namespace fuseraft.Core.Models;

/// <summary>
/// Controls how much knowledge retrieval the context assembly pipeline performs
/// for an agent. Influences breadth of retrieval, not whether it occurs —
/// retrieval is always on unless explicitly disabled.
/// </summary>
public enum KnowledgeWeight
{
    /// <summary>
    /// Skip knowledge retrieval entirely. Use only for performance-critical agents
    /// (e.g. a fast triage agent) that do not benefit from prior knowledge.
    /// </summary>
    None = 0,

    /// <summary>
    /// Retrieve only high-confidence (Verified/Inferred) items.
    /// Suitable for focused agents with tight context budgets.
    /// </summary>
    Low = 1,

    /// <summary>
    /// Standard retrieval across all confidence tiers. Default for all agents.
    /// </summary>
    Default = 2,

    /// <summary>
    /// Broader retrieval with graph-neighbour expansion.
    /// Every seed symbol is expanded one hop in the repository graph so dependent
    /// types, call-sites, and governing ADRs are included automatically.
    /// Use for investigation and refactoring agents that need wide context.
    /// </summary>
    High = 3,
}
