using System.ComponentModel;
using System.Text;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Agent-facing tools for the repository semantic graph.
///
/// Tool names (via <c>graph_</c> prefix):
///   graph_search     — find nodes by name or type
///   graph_refs       — what references a given symbol (inbound references edges)
///   graph_dependents — transitive dependents of a symbol (inbound depends_on edges)
/// </summary>
public sealed class GraphPlugin
{
    private readonly RepositoryGraphStore _store;

    public GraphPlugin(RepositoryGraphStore store) => _store = store;

    [Description("Search the repository graph for nodes by name, type, or file path.")]
    public async Task<string> SearchAsync(
        [Description("Partial name to match against node names. Leave empty to list all.")]
        string query = "",
        [Description("Node kind to filter by: File, Namespace, Package, Type, Interface, Method, Property, Field, or Adr.")]
        string? kind = null,
        [Description("Relative file path to restrict results to a single file.")]
        string? file = null)
    {
        var graph = await _store.LoadAsync();

        NodeType? kindFilter = null;
        if (kind is not null && Enum.TryParse<NodeType>(kind, ignoreCase: true, out var parsed))
            kindFilter = parsed;

        var results = graph.Nodes.AsEnumerable();
        if (kindFilter.HasValue)
            results = results.Where(n => n.Kind == kindFilter.Value);
        if (file is not null)
            results = results.Where(n => n.FilePath is not null &&
                n.FilePath.Contains(file, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query))
            results = results.Where(n =>
                (n.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (n.Id.Contains(query, StringComparison.OrdinalIgnoreCase)));

        var list = results.Take(50).ToList();
        if (list.Count == 0) return PluginResult.NotFound("No matching graph nodes found.");

        var sb = new StringBuilder();
        sb.AppendLine($"=== Graph nodes ({list.Count} result(s)) ===");
        foreach (var n in list)
        {
            sb.Append($"  [{n.Kind}] {n.Id}");
            if (n.FilePath is not null) sb.Append($"  file: {n.FilePath}");
            if (n.StartLine.HasValue)   sb.Append($":{n.StartLine}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Find all graph nodes that reference the given symbol ID.")]
    public async Task<string> RefsAsync(
        [Description("SymbolId of the target node (e.g. type:fuseraft.Core.Models.AdrEntry).")]
        string symbolId)
    {
        if (string.IsNullOrWhiteSpace(symbolId))
            return PluginResult.Error("symbolId must not be empty.");

        var graph = await _store.LoadAsync();
        var edges = graph.EdgesTo(symbolId, EdgeType.References)
            .Concat(graph.EdgesTo(symbolId, EdgeType.Implements))
            .Concat(graph.EdgesTo(symbolId, EdgeType.Inherits))
            .ToList();

        if (edges.Count == 0)
            return PluginResult.NotFound($"No references found for '{symbolId}'.");

        var sb = new StringBuilder();
        sb.AppendLine($"=== References to {symbolId} ({edges.Count}) ===");
        foreach (var e in edges)
        {
            var fromNode = graph.FindById(e.From);
            sb.AppendLine($"  [{e.Relation}] {e.From}" +
                          (fromNode?.FilePath is not null ? $"  ({fromNode.FilePath}:{fromNode.StartLine})" : ""));
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Find transitive dependents of a symbol — nodes that depend_on or reference it directly or indirectly.")]
    public async Task<string> DependentsAsync(
        [Description("SymbolId of the root node (e.g. type:fuseraft.Core.Models.AdrEntry).")]
        string symbolId,
        [Description("Maximum traversal depth. Defaults to 3.")]
        int depth = 3)
    {
        if (string.IsNullOrWhiteSpace(symbolId))
            return PluginResult.Error("symbolId must not be empty.");

        var graph = await _store.LoadAsync();
        if (depth < 1) depth = 1;
        if (depth > 10) depth = 10;

        var visited  = new HashSet<string>(StringComparer.Ordinal) { symbolId };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { symbolId };
        var results  = new List<(string From, string Relation, int Level)>();

        for (int d = 1; d <= depth && frontier.Count > 0; d++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in frontier)
            {
                var inbound = graph.EdgesTo(id, EdgeType.DependsOn)
                    .Concat(graph.EdgesTo(id, EdgeType.References))
                    .Concat(graph.EdgesTo(id, EdgeType.Implements))
                    .Concat(graph.EdgesTo(id, EdgeType.Inherits));

                foreach (var e in inbound)
                {
                    if (!visited.Add(e.From)) continue;
                    results.Add((e.From, e.Relation, d));
                    next.Add(e.From);
                }
            }
            frontier = next;
        }

        if (results.Count == 0)
            return PluginResult.NotFound($"No dependents found for '{symbolId}'.");

        var sb = new StringBuilder();
        sb.AppendLine($"=== Dependents of {symbolId} (depth {depth}) ===");
        foreach (var (from, rel, level) in results)
        {
            var node = graph.FindById(from);
            sb.AppendLine($"  [depth={level}] [{rel}] {from}" +
                          (node?.FilePath is not null ? $"  ({node.FilePath}:{node.StartLine})" : ""));
        }
        return sb.ToString().TrimEnd();
    }
}
