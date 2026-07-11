using fuseraft.Core.Models.Orchestration;
using fuseraft.Orchestration.Graph;

namespace FuseraftCli.Tests;

/// <summary>
/// Regression coverage for <see cref="GraphTopology.ComputeBackEdges"/> — the DFS-based
/// forward/back edge classification that replaced an earlier BFS-shortest-path-layer
/// approximation. The approximation misclassified a legitimate forward edge as a back-edge
/// whenever two forward paths of different lengths converged on the same node.
/// </summary>
public sealed class GraphOrchestratorBackEdgeTests
{
    private static Dictionary<string, List<GraphEdgeConfig>> EdgesBySource(params (string From, string To)[] edges) =>
        edges
            .Select(e => new GraphEdgeConfig { From = e.From, To = e.To })
            .GroupBy(e => e.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void DiamondConvergence_LongerPathIntoSharedNode_IsNotMisclassifiedAsBackEdge()
    {
        // A -> B -> D            (length 2 into D)
        // A -> C -> E -> D       (length 3 into D)
        // The old BFS-layer approximation assigned layer(D) = 2 (via A->B->D, discovered
        // first) and layer(E) = 2 (via A->C->E). Edge E->D then had toLayer(D)=2 <=
        // fromLayer(E)=2, so it was wrongly classified as a back-edge even though E->D never
        // closes a cycle back to an ancestor.
        var edges = EdgesBySource(
            ("A", "B"), ("B", "D"),
            ("A", "C"), ("C", "E"), ("E", "D"));

        var backEdges = GraphTopology.ComputeBackEdges("A", edges);

        Assert.Empty(backEdges);
    }

    [Fact]
    public void GenuineCycle_EdgeBackToAnAncestor_IsClassifiedAsBackEdge()
    {
        // A -> B -> D -> A is a real cycle; D->A must still be a back-edge.
        var edges = EdgesBySource(("A", "B"), ("B", "D"), ("D", "A"));

        var backEdges = GraphTopology.ComputeBackEdges("A", edges);

        Assert.Contains(GraphTopology.EdgeKey("D", "A"), backEdges);
        Assert.DoesNotContain(GraphTopology.EdgeKey("A", "B"), backEdges);
        Assert.DoesNotContain(GraphTopology.EdgeKey("B", "D"), backEdges);
    }

    [Fact]
    public void DiamondConvergence_PlusGenuineCycleFromTheConvergedNode_BothClassifiedCorrectly()
    {
        // Combines both shapes: the diamond into D, plus a real cycle D -> A.
        var edges = EdgesBySource(
            ("A", "B"), ("B", "D"),
            ("A", "C"), ("C", "E"), ("E", "D"),
            ("D", "A"));

        var backEdges = GraphTopology.ComputeBackEdges("A", edges);

        Assert.Contains(GraphTopology.EdgeKey("D", "A"), backEdges);
        Assert.DoesNotContain(GraphTopology.EdgeKey("E", "D"), backEdges);
        Assert.DoesNotContain(GraphTopology.EdgeKey("B", "D"), backEdges);
    }
}
