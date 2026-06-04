using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Orchestration;

/// <summary>
/// Expands a set of seed symbol names into related symbols by traversing one hop
/// in the repository semantic graph.
///
/// <para>
/// Traversal follows edges in both directions:
/// <list type="bullet">
///   <item><c>defines</c>, <c>implements</c>, <c>inherits</c> — structural relationships.</item>
///   <item><c>references</c>, <c>depends_on</c> — usage relationships.</item>
/// </list>
/// ADR-governs edges are intentionally excluded; those are surfaced separately via
/// <c>adr_graph</c> context sources.
/// </para>
/// </summary>
public sealed class GraphExpansionRetriever(RepositoryGraphStore graphStore)
{
    private static readonly HashSet<string> ExpandRelations = new(StringComparer.OrdinalIgnoreCase)
    {
        EdgeType.Defines,
        EdgeType.Implements,
        EdgeType.Inherits,
        EdgeType.References,
        EdgeType.DependsOn,
    };

    /// <summary>
    /// Returns additional symbol-name query terms derived by expanding
    /// <paramref name="seedSymbols"/> one hop in the repository graph.
    /// The original seeds are not included in the result (callers already have them).
    /// </summary>
    public async Task<IReadOnlyList<string>> ExpandAsync(
        IReadOnlyList<string> seedSymbols,
        int                   maxExpansion = 15,
        CancellationToken     ct           = default)
    {
        if (seedSymbols.Count == 0) return [];

        RepositoryGraph graph;
        try   { graph = await graphStore.LoadAsync(ct); }
        catch { return []; }

        if (graph.Nodes.Count == 0) return [];

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seedSymbols)
        {
            // Match any node whose name contains the seed symbol (case-insensitive).
            var matchedNodes = graph.Nodes
                .Where(n => n.Name is not null &&
                            n.Name.Contains(seed, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var node in matchedNodes)
            {
                foreach (var edge in graph.EdgesFrom(node.Id))
                {
                    if (!ExpandRelations.Contains(edge.Relation)) continue;
                    var target = graph.FindById(edge.To);
                    if (target?.Name is { Length: > 0 } name) expanded.Add(name);
                }
                foreach (var edge in graph.EdgesTo(node.Id))
                {
                    if (!ExpandRelations.Contains(edge.Relation)) continue;
                    var source = graph.FindById(edge.From);
                    if (source?.Name is { Length: > 0 } name) expanded.Add(name);
                }
            }
        }

        // Remove the original seeds from the expansion so the caller doesn't double-query.
        foreach (var seed in seedSymbols)
            expanded.Remove(seed);

        return expanded.Take(maxExpansion).ToList();
    }
}
