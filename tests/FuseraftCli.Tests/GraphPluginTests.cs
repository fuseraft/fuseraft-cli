using fuseraft.Core.Models.Repository;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Infrastructure.Repository;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="GraphPlugin"/> — the agent-facing read surface over the repository
/// semantic graph (<see cref="RepositoryGraphStore"/>/<see cref="RepositoryGraph"/>).
/// </summary>
public sealed class GraphPluginTests : IDisposable
{
    private readonly string _root;
    private readonly string _graphPath;

    public GraphPluginTests()
    {
        _root      = Path.Combine(Path.GetTempPath(), "fuseraft_graph_plugin_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _graphPath = Path.Combine(_root, "repository.graph");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<GraphPlugin> NewPluginWithGraphAsync(RepositoryGraph graph)
    {
        var store = new RepositoryGraphStore(_graphPath);
        await store.SaveAsync(graph);
        return new GraphPlugin(store);
    }

    private static RepositoryGraphNode Node(string id, NodeType kind, string? name = null, string? filePath = null, int? startLine = null) =>
        new() { Id = id, Kind = kind, Name = name ?? id, FilePath = filePath, StartLine = startLine };

    [Fact]
    public async Task SearchAsync_EmptyQuery_ListsAllNodes()
    {
        var graph = new RepositoryGraph
        {
            Nodes =
            [
                Node("type:A", NodeType.Type),
                Node("type:B", NodeType.Type),
            ],
        };
        var plugin = await NewPluginWithGraphAsync(graph);

        var result = await plugin.SearchAsync();

        Assert.Contains("type:A", result);
        Assert.Contains("type:B", result);
        Assert.Contains("2 result(s)", result);
    }

    [Fact]
    public async Task SearchAsync_NoNodes_ReturnsNotFound()
    {
        var plugin = await NewPluginWithGraphAsync(new RepositoryGraph());

        var result = await plugin.SearchAsync();

        Assert.StartsWith("[NOT FOUND]", result);
    }

    [Fact]
    public async Task SearchAsync_QueryFiltersByNameSubstring()
    {
        var graph = new RepositoryGraph
        {
            Nodes =
            [
                Node("type:Foo", NodeType.Type, name: "Foo"),
                Node("type:Bar", NodeType.Type, name: "Bar"),
            ],
        };
        var plugin = await NewPluginWithGraphAsync(graph);

        var result = await plugin.SearchAsync(query: "foo");

        Assert.Contains("type:Foo", result);
        Assert.DoesNotContain("type:Bar", result);
    }

    [Fact]
    public async Task SearchAsync_KindFilter_OnlyMatchingKindReturned()
    {
        var graph = new RepositoryGraph
        {
            Nodes =
            [
                Node("type:Foo", NodeType.Type),
                Node("method:Foo.Bar", NodeType.Method),
            ],
        };
        var plugin = await NewPluginWithGraphAsync(graph);

        var result = await plugin.SearchAsync(kind: "Method");

        Assert.Contains("method:Foo.Bar", result);
        Assert.DoesNotContain("type:Foo", result);
    }

    [Fact]
    public async Task SearchAsync_InvalidKind_IgnoredNotError()
    {
        var graph = new RepositoryGraph { Nodes = [Node("type:Foo", NodeType.Type)] };
        var plugin = await NewPluginWithGraphAsync(graph);

        // An unparseable kind falls back to no kind filter rather than erroring, per
        // GraphPlugin.SearchAsync's Enum.TryParse-and-ignore-on-failure handling.
        var result = await plugin.SearchAsync(kind: "NotARealKind");

        Assert.Contains("type:Foo", result);
    }

    [Fact]
    public async Task SearchAsync_FileFilter_RestrictsToMatchingPath()
    {
        var graph = new RepositoryGraph
        {
            Nodes =
            [
                Node("type:Foo", NodeType.Type, filePath: "src/Foo.cs"),
                Node("type:Bar", NodeType.Type, filePath: "src/Bar.cs"),
            ],
        };
        var plugin = await NewPluginWithGraphAsync(graph);

        var result = await plugin.SearchAsync(file: "Foo.cs");

        Assert.Contains("type:Foo", result);
        Assert.DoesNotContain("type:Bar", result);
    }

    [Fact]
    public async Task RefsAsync_EmptySymbolId_ReturnsError()
    {
        var plugin = await NewPluginWithGraphAsync(new RepositoryGraph());

        var result = await plugin.RefsAsync("");

        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task RefsAsync_NoReferences_ReturnsNotFound()
    {
        var graph = new RepositoryGraph { Nodes = [Node("type:Foo", NodeType.Type)] };
        var plugin = await NewPluginWithGraphAsync(graph);

        var result = await plugin.RefsAsync("type:Foo");

        Assert.StartsWith("[NOT FOUND]", result);
    }

    [Fact]
    public async Task RefsAsync_FindsReferencesImplementsAndInherits()
    {
        var graph = new RepositoryGraph
        {
            Nodes =
            [
                Node("type:Target", NodeType.Type, filePath: "src/Target.cs", startLine: 10),
                Node("type:Caller", NodeType.Type, filePath: "src/Caller.cs"),
                Node("type:Impl", NodeType.Type),
            ],
            Edges =
            [
                new RepositoryGraphEdge { From = "type:Caller", To = "type:Target", Relation = EdgeType.References },
                new RepositoryGraphEdge { From = "type:Impl",   To = "type:Target", Relation = EdgeType.Implements },
                new RepositoryGraphEdge { From = "type:Unrelated", To = "type:Target", Relation = EdgeType.Imports },
            ],
        };
        var plugin = await NewPluginWithGraphAsync(graph);

        var result = await plugin.RefsAsync("type:Target");

        Assert.Contains("type:Caller", result);
        Assert.Contains("type:Impl", result);
        Assert.Contains("src/Caller.cs", result);
        Assert.DoesNotContain("type:Unrelated", result);
    }

    [Fact]
    public async Task DependentsAsync_EmptySymbolId_ReturnsError()
    {
        var plugin = await NewPluginWithGraphAsync(new RepositoryGraph());

        var result = await plugin.DependentsAsync("");

        Assert.StartsWith("[ERROR]", result);
    }

    [Fact]
    public async Task DependentsAsync_NoDependents_ReturnsNotFound()
    {
        var graph = new RepositoryGraph { Nodes = [Node("type:Root", NodeType.Type)] };
        var plugin = await NewPluginWithGraphAsync(graph);

        var result = await plugin.DependentsAsync("type:Root");

        Assert.StartsWith("[NOT FOUND]", result);
    }

    [Fact]
    public async Task DependentsAsync_TransitiveChain_FoundWithinDepth()
    {
        // C depends_on B depends_on A(root) — B is depth 1, C is depth 2.
        var graph = new RepositoryGraph
        {
            Nodes = [Node("A", NodeType.Type), Node("B", NodeType.Type), Node("C", NodeType.Type)],
            Edges =
            [
                new RepositoryGraphEdge { From = "B", To = "A", Relation = EdgeType.DependsOn },
                new RepositoryGraphEdge { From = "C", To = "B", Relation = EdgeType.DependsOn },
            ],
        };
        var plugin = await NewPluginWithGraphAsync(graph);

        var result = await plugin.DependentsAsync("A", depth: 3);

        Assert.Contains("[depth=1] [depends_on] B", result);
        Assert.Contains("[depth=2] [depends_on] C", result);
    }

    [Fact]
    public async Task DependentsAsync_DepthLimitsTraversal()
    {
        var graph = new RepositoryGraph
        {
            Nodes = [Node("A", NodeType.Type), Node("B", NodeType.Type), Node("C", NodeType.Type)],
            Edges =
            [
                new RepositoryGraphEdge { From = "B", To = "A", Relation = EdgeType.DependsOn },
                new RepositoryGraphEdge { From = "C", To = "B", Relation = EdgeType.DependsOn },
            ],
        };
        var plugin = await NewPluginWithGraphAsync(graph);

        var result = await plugin.DependentsAsync("A", depth: 1);

        Assert.Contains("B", result);
        Assert.DoesNotContain("C", result);
    }

    [Fact]
    public async Task DependentsAsync_DepthClampedToValidRange()
    {
        var graph = new RepositoryGraph
        {
            Nodes = [Node("A", NodeType.Type), Node("B", NodeType.Type)],
            Edges = [new RepositoryGraphEdge { From = "B", To = "A", Relation = EdgeType.DependsOn }],
        };
        var plugin = await NewPluginWithGraphAsync(graph);

        // depth <= 0 is clamped to 1 rather than treated as "no traversal".
        var result = await plugin.DependentsAsync("A", depth: 0);

        Assert.Contains("B", result);
    }

    [Fact]
    public async Task DependentsAsync_CycleDoesNotInfiniteLoop()
    {
        var graph = new RepositoryGraph
        {
            Nodes = [Node("A", NodeType.Type), Node("B", NodeType.Type)],
            Edges =
            [
                new RepositoryGraphEdge { From = "B", To = "A", Relation = EdgeType.DependsOn },
                new RepositoryGraphEdge { From = "A", To = "B", Relation = EdgeType.DependsOn },
            ],
        };
        var plugin = await NewPluginWithGraphAsync(graph);

        // Must terminate (visited-set guard) rather than looping forever on the A<->B cycle.
        var result = await plugin.DependentsAsync("A", depth: 10);

        Assert.Contains("B", result);
    }
}
