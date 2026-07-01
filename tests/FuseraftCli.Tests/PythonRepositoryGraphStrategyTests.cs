using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers <see cref="PythonRepositoryGraphStrategy"/> via the same
/// write-file/BuildAllAsync/assert-on-graph flow used by
/// <see cref="KnowledgeLayerRoundTripTests"/>'s Stage1 test, but scoped to Python-specific
/// declarations (module identity, classes/inheritance, methods, free functions, imports).
/// </summary>
public sealed class PythonRepositoryGraphStrategyTests : IDisposable
{
    private readonly string _root;
    private readonly string _src;
    private readonly RepositoryGraphStore _graphStore;
    private readonly RepositoryGraphBuilder _graphBuilder;

    public PythonRepositoryGraphStrategyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fuseraft_py_{Guid.NewGuid():N}");
        _src  = Path.Combine(_root, "src");
        Directory.CreateDirectory(_src);

        var graphPath = Path.Combine(_root, "repository.graph");
        _graphStore   = new RepositoryGraphStore(graphPath);
        _graphBuilder = new RepositoryGraphBuilder(
            _graphStore, _root, strategies: [new PythonRepositoryGraphStrategy()]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ModuleIdentity_ProducesPackageNodeAndDefinesEdge()
    {
        WriteSourceFile("widgets.py", "X = 1\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        var moduleNode = graph.FindById("package:widgets");
        Assert.NotNull(moduleNode);
        Assert.Equal(NodeType.Package, moduleNode!.Kind);
        Assert.Contains(graph.Edges, e =>
            e.From == "file:widgets.py" && e.To == "package:widgets" && e.Relation == EdgeType.Defines);
    }

    [Fact]
    public async Task InitPy_ModuleIdentityIsTheEnclosingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_src, "zoo"));
        WriteSourceFile(Path.Combine("zoo", "__init__.py"), "\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        Assert.NotNull(graph.FindById("package:zoo"));
    }

    [Fact]
    public async Task ClassWithBase_ProducesInheritsEdge()
    {
        WriteSourceFile("animals.py",
            "class Animal:\n" +
            "    name: str\n\n" +
            "class Dog(Animal):\n" +
            "    breed: str\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        Assert.Contains(graph.Nodes, n => n.Kind == NodeType.Type && n.Name == "Dog");
        Assert.Contains(graph.Nodes, n => n.Kind == NodeType.Field && n.Name == "breed");
        Assert.Contains(graph.Edges, e =>
            e.From == "type:animals.Dog" && e.To == "type:animals.Animal" && e.Relation == EdgeType.Inherits);
    }

    [Fact]
    public async Task MultiLineBaseList_IsStillParsed()
    {
        WriteSourceFile("shapes.py",
            "class Base1:\n" +
            "    pass\n\n" +
            "class Base2:\n" +
            "    pass\n\n" +
            "class Shape(\n" +
            "    Base1,\n" +
            "    Base2,\n" +
            "):\n" +
            "    sides: int\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        Assert.NotNull(graph.FindById("type:shapes.Shape"));
        Assert.Contains(graph.Edges, e =>
            e.From == "type:shapes.Shape" && e.To == "type:shapes.Base1" && e.Relation == EdgeType.Inherits);
        Assert.Contains(graph.Edges, e =>
            e.From == "type:shapes.Shape" && e.To == "type:shapes.Base2" && e.Relation == EdgeType.Inherits);
    }

    [Fact]
    public async Task Method_IsScopedToClass_NotFreeFunction()
    {
        WriteSourceFile("dog.py",
            "class Dog:\n" +
            "    def bark(self) -> str:\n" +
            "        return \"Woof\"\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        var methodNode = graph.FindById("method:dog.Dog.bark");
        Assert.NotNull(methodNode);
        Assert.Contains(graph.Edges, e =>
            e.From == "type:dog.Dog" && e.To == "method:dog.Dog.bark" && e.Relation == EdgeType.Defines);
    }

    [Fact]
    public async Task FreeFunction_IsScopedToModule()
    {
        WriteSourceFile("mathutil.py",
            "def total(a, b):\n" +
            "    return a + b\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        var methodNode = graph.FindById("method:mathutil.total");
        Assert.NotNull(methodNode);
        Assert.Contains(graph.Edges, e =>
            e.From == "package:mathutil" && e.To == "method:mathutil.total" && e.Relation == EdgeType.Defines);
    }

    [Fact]
    public async Task AbsoluteAndRelativeImports_ProduceImportsEdges()
    {
        Directory.CreateDirectory(Path.Combine(_src, "app"));
        WriteSourceFile(Path.Combine("app", "service.py"),
            "import os\n" +
            "from . import models\n" +
            "from .util import helper\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        // "from . import models" resolves to the current package itself ("app") — the
        // imported name isn't modeled separately, since it may be a submodule or a symbol.
        Assert.Contains(graph.Edges, e =>
            e.From == "file:app/service.py" && e.To == "package:os" && e.Relation == EdgeType.Imports);
        Assert.Contains(graph.Edges, e =>
            e.From == "file:app/service.py" && e.To == "package:app" && e.Relation == EdgeType.Imports);
        Assert.Contains(graph.Edges, e =>
            e.From == "file:app/service.py" && e.To == "package:app.util" && e.Relation == EdgeType.Imports);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string WriteSourceFile(string name, string content)
    {
        var path = Path.Combine(_src, name);
        File.WriteAllText(path, content);
        return path;
    }
}
