namespace fuseraft.Core.Models.Repository;

/// <summary>
/// A single node in the repository semantic graph.
/// <para>
/// Identity is stable across rebuilds: <see cref="Id"/> is the fully-qualified
/// <c>SymbolId</c> string (e.g. <c>type:fuseraft.Core.Models.AdrEntry</c>).
/// Node IDs survive renames only when git-history correlation is applied; for the
/// initial implementation stable IDs are guaranteed within a session.
/// </para>
/// </summary>
public sealed record RepositoryGraphNode
{
    public string     Id        { get; init; } = string.Empty;
    public NodeType   Kind      { get; init; }
    public string?    FilePath  { get; init; }
    public string?    Name      { get; init; }
    public string?    Namespace { get; init; }
    public int?       StartLine { get; init; }
    public int?       EndLine   { get; init; }
    public string?    SessionId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A directed edge between two graph nodes.
/// </summary>
public sealed record RepositoryGraphEdge
{
    /// <summary>Source node <see cref="RepositoryGraphNode.Id"/>.</summary>
    public string From     { get; init; } = string.Empty;
    /// <summary>Target node <see cref="RepositoryGraphNode.Id"/>.</summary>
    public string To       { get; init; } = string.Empty;
    /// <summary>Semantic relation. Use <see cref="EdgeType"/> constants.</summary>
    public string Relation { get; init; } = string.Empty;
}

/// <summary>
/// Well-known edge relation labels for the repository semantic graph.
/// </summary>
public static class EdgeType
{
    public const string Defines    = "defines";
    public const string Imports    = "imports";
    public const string Inherits   = "inherits";
    public const string Implements = "implements";
    public const string References = "references";
    public const string DependsOn  = "depends_on";
    public const string AdrGoverns = "adr_governs";
}

/// <summary>
/// The complete in-memory repository semantic graph (nodes + edges).
/// </summary>
public sealed class RepositoryGraph
{
    public List<RepositoryGraphNode> Nodes       { get; set; } = [];
    public List<RepositoryGraphEdge> Edges       { get; set; } = [];
    public DateTimeOffset            LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    // ── Lookup helpers ──────────────────────────────────────────────────────

    public RepositoryGraphNode? FindById(string id) =>
        Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));

    /// <summary>Returns all nodes whose <c>Id</c> starts with the given SymbolId prefix.</summary>
    public IEnumerable<RepositoryGraphNode> FindByFile(string filePath) =>
        Nodes.Where(n => string.Equals(n.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns all edges with the given relation type leaving <paramref name="fromId"/>.</summary>
    public IEnumerable<RepositoryGraphEdge> EdgesFrom(string fromId, string? relation = null) =>
        Edges.Where(e => string.Equals(e.From, fromId, StringComparison.Ordinal)
                      && (relation is null || string.Equals(e.Relation, relation, StringComparison.Ordinal)));

    /// <summary>Returns all edges with the given relation type arriving at <paramref name="toId"/>.</summary>
    public IEnumerable<RepositoryGraphEdge> EdgesTo(string toId, string? relation = null) =>
        Edges.Where(e => string.Equals(e.To, toId, StringComparison.Ordinal)
                      && (relation is null || string.Equals(e.Relation, relation, StringComparison.Ordinal)));

    // ── Mutation helpers ────────────────────────────────────────────────────

    /// <summary>Removes all nodes and edges associated with <paramref name="filePath"/>.</summary>
    public void RemoveFile(string filePath)
    {
        var ids = new HashSet<string>(
            Nodes.Where(n => string.Equals(n.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                 .Select(n => n.Id),
            StringComparer.Ordinal);

        Nodes.RemoveAll(n => ids.Contains(n.Id));
        Edges.RemoveAll(e => ids.Contains(e.From) || ids.Contains(e.To));
    }

    public void AddNode(RepositoryGraphNode node)
    {
        Nodes.RemoveAll(n => string.Equals(n.Id, node.Id, StringComparison.Ordinal));
        Nodes.Add(node);
    }

    public void AddEdge(RepositoryGraphEdge edge)
    {
        bool exists = Edges.Any(e =>
            string.Equals(e.From, edge.From, StringComparison.Ordinal) &&
            string.Equals(e.To,   edge.To,   StringComparison.Ordinal) &&
            string.Equals(e.Relation, edge.Relation, StringComparison.Ordinal));
        if (!exists) Edges.Add(edge);
    }
}
