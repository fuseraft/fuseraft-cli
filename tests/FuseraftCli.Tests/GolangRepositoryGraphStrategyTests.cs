using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers <see cref="GolangRepositoryGraphStrategy"/> via the same
/// write-file/BuildAllAsync/assert-on-graph flow used by
/// <see cref="KnowledgeLayerRoundTripTests"/>'s Stage1 test, but scoped to Go-specific
/// declarations (package, struct/interface, embedding, receiver and free functions, imports).
/// </summary>
public sealed class GolangRepositoryGraphStrategyTests : IDisposable
{
    private readonly string _root;
    private readonly string _src;
    private readonly RepositoryGraphStore _graphStore;
    private readonly RepositoryGraphBuilder _graphBuilder;

    public GolangRepositoryGraphStrategyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fuseraft_go_{Guid.NewGuid():N}");
        _src  = Path.Combine(_root, "src");
        Directory.CreateDirectory(_src);

        var graphPath = Path.Combine(_root, "repository.graph");
        _graphStore   = new RepositoryGraphStore(graphPath);
        _graphBuilder = new RepositoryGraphBuilder(
            _graphStore, _root, strategies: [new GolangRepositoryGraphStrategy()]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task PackageDeclaration_ProducesPackageNodeAndDefinesEdge()
    {
        WriteSourceFile("pkg.go",
            "package widgets\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        var pkgNode = graph.FindById("package:widgets");
        Assert.NotNull(pkgNode);
        Assert.Equal(NodeType.Package, pkgNode!.Kind);
        Assert.Contains(graph.Edges, e =>
            e.From == "file:pkg.go" && e.To == "package:widgets" && e.Relation == EdgeType.Defines);
    }

    [Fact]
    public async Task StructWithEmbeddedField_ProducesInheritsEdge()
    {
        WriteSourceFile("animals.go",
            "package zoo\n\n" +
            "type Animal struct {\n" +
            "    Name string\n" +
            "}\n\n" +
            "type Dog struct {\n" +
            "    Animal\n" +
            "    Breed string\n" +
            "}\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        Assert.Contains(graph.Nodes, n => n.Kind == NodeType.Type && n.Name == "Dog");
        Assert.Contains(graph.Nodes, n => n.Kind == NodeType.Field && n.Name == "Breed");
        Assert.Contains(graph.Edges, e =>
            e.From == "type:zoo.Dog" && e.To == "type:zoo.Animal" && e.Relation == EdgeType.Inherits);
    }

    [Fact]
    public async Task InterfaceDeclaration_ProducesInterfaceNode()
    {
        WriteSourceFile("shape.go",
            "package geo\n\n" +
            "type Shape interface {\n" +
            "    Area() float64\n" +
            "}\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        var node = graph.FindById("interface:geo.Shape");
        Assert.NotNull(node);
        Assert.Equal(NodeType.Interface, node!.Kind);
    }

    [Fact]
    public async Task ReceiverMethod_IsScopedToReceiverType()
    {
        WriteSourceFile("dog.go",
            "package zoo\n\n" +
            "type Dog struct {\n" +
            "    Name string\n" +
            "}\n\n" +
            "func (d *Dog) Bark() string {\n" +
            "    return \"Woof\"\n" +
            "}\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        var methodNode = graph.FindById("method:zoo.Dog.Bark");
        Assert.NotNull(methodNode);
        Assert.Equal(NodeType.Method, methodNode!.Kind);
        Assert.Contains(graph.Edges, e =>
            e.From == "type:zoo.Dog" && e.To == "method:zoo.Dog.Bark" && e.Relation == EdgeType.Defines);
    }

    [Fact]
    public async Task FreeFunction_IsScopedToPackage()
    {
        WriteSourceFile("math.go",
            "package mathutil\n\n" +
            "func Sum(a int, b int) int {\n" +
            "    return a + b\n" +
            "}\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        var methodNode = graph.FindById("method:mathutil.Sum");
        Assert.NotNull(methodNode);
        Assert.Contains(graph.Edges, e =>
            e.From == "package:mathutil" && e.To == "method:mathutil.Sum" && e.Relation == EdgeType.Defines);
    }

    [Fact]
    public async Task GroupedImports_ProduceImportsEdgesForEachEntry()
    {
        WriteSourceFile("io.go",
            "package app\n\n" +
            "import (\n" +
            "    \"fmt\"\n" +
            "    \"os\"\n" +
            ")\n");

        await _graphBuilder.BuildAllAsync(_src);
        var graph = await _graphStore.LoadAsync();

        Assert.Contains(graph.Edges, e =>
            e.From == "file:io.go" && e.To == "package:fmt" && e.Relation == EdgeType.Imports);
        Assert.Contains(graph.Edges, e =>
            e.From == "file:io.go" && e.To == "package:os" && e.Relation == EdgeType.Imports);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string WriteSourceFile(string name, string content)
    {
        var path = Path.Combine(_src, name);
        File.WriteAllText(path, content);
        return path;
    }
}
