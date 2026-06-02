using System.Text.RegularExpressions;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// Builds and incrementally maintains the <see cref="RepositoryGraph"/> by scanning C# source files.
///
/// <para>
/// Uses structural text analysis (regex over source lines) to extract symbol declarations —
/// no Roslyn dependency required for the initial build. Each file is scanned in isolation so
/// incremental rebuilds update only the nodes in the changed file.
/// </para>
///
/// <para>
/// SymbolId scheme (stable, fully-qualified names):
/// <list type="bullet">
///   <item><c>file:relative/path/to/File.cs</c></item>
///   <item><c>namespace:My.Namespace</c></item>
///   <item><c>type:My.Namespace.ClassName</c></item>
///   <item><c>interface:My.Namespace.IName</c></item>
///   <item><c>method:My.Namespace.ClassName.MethodName</c></item>
///   <item><c>property:My.Namespace.ClassName.PropName</c></item>
///   <item><c>field:My.Namespace.ClassName.FieldName</c></item>
///   <item><c>adr:ADR-NNNN</c></item>
/// </list>
/// </para>
/// </summary>
public sealed class RepositoryGraphBuilder
{
    private readonly RepositoryGraphStore _store;
    private readonly string _projectRoot;
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    // Structural patterns for C# source
    private static readonly Regex NamespaceRx     = new(@"^\s*(?:file\s+)?namespace\s+([\w.]+)", RegexOptions.Compiled);
    private static readonly Regex UsingRx         = new(@"^\s*using\s+([\w.]+)\s*;",             RegexOptions.Compiled);
    private static readonly Regex ClassRx         = new(@"(?:^|\s)(?:public|internal|private|protected)(?:\s+(?:abstract|sealed|static|partial|record|readonly))*\s+class\s+(\w+)(?:\s*<[^>]*>)?\s*(?::\s*([\w,\s<>.]+?))?(?:\s*where|\s*\{|$)", RegexOptions.Compiled);
    private static readonly Regex InterfaceRx     = new(@"(?:^|\s)(?:public|internal|private|protected)(?:\s+partial)?\s+interface\s+(\w+)(?:\s*<[^>]*>)?\s*(?::\s*([\w,\s<>.]+?))?(?:\s*where|\s*\{|$)", RegexOptions.Compiled);
    private static readonly Regex MethodRx        = new(@"^\s*(?:public|internal|private|protected)(?:\s+(?:static|virtual|abstract|override|sealed|async|extern|new))*\s+[\w<>?\[\].,\s]+\s+(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex PropertyRx      = new(@"^\s*(?:public|internal|private|protected)(?:\s+(?:static|virtual|abstract|override|sealed|new|required))*\s+[\w<>?\[\].,\s]+\s+(\w+)\s*\{", RegexOptions.Compiled);
    private static readonly Regex FieldRx         = new(@"^\s*(?:public|internal|private|protected)(?:\s+(?:static|readonly|const|volatile|new))*\s+[\w<>?\[\].,\s]+\s+(_?\w+)\s*(?:=|;)", RegexOptions.Compiled);
    private static readonly Regex RecordRx        = new(@"(?:^|\s)(?:public|internal|private|protected)(?:\s+(?:abstract|sealed|partial))*\s+record\s+(?:class\s+|struct\s+)?(\w+)(?:\s*<[^>]*>)?\s*(?:\(|:\s*([\w,\s<>.]+?))?\s*(?:where|\{|$)", RegexOptions.Compiled);

    public RepositoryGraphBuilder(RepositoryGraphStore store, string? projectRoot = null)
    {
        _store       = store;
        _projectRoot = Path.GetFullPath(projectRoot ?? Directory.GetCurrentDirectory());
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds nodes for <paramref name="absoluteFilePath"/> in the persisted graph.
    /// Removes stale nodes first, then re-scans the file and saves.
    /// No-ops for non-.cs files.
    /// </summary>
    public async Task RebuildFileAsync(string absoluteFilePath, CancellationToken ct = default)
    {
        if (!absoluteFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(absoluteFilePath)) return;

        await _buildLock.WaitAsync(ct);
        try
        {
            var graph    = await _store.LoadAsync(ct);
            var relative = RelativePath(absoluteFilePath);
            graph.RemoveFile(relative);
            ScanFile(absoluteFilePath, relative, graph);
            await _store.SaveAsync(graph, ct);
        }
        finally { _buildLock.Release(); }
    }

    /// <summary>
    /// Full initial build: scans all .cs files under <paramref name="directory"/> (or the project
    /// root when omitted) and overwrites the persisted graph.
    /// Returns the number of nodes created.
    /// </summary>
    public async Task<(int Nodes, int Edges)> BuildAllAsync(
        string? directory = null,
        CancellationToken ct = default)
    {
        var root  = directory is not null ? Path.GetFullPath(directory) : _projectRoot;
        var graph = new RepositoryGraph();
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                             .Where(f => !IsBuildArtifact(f))
                             .ToList();

        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) break;
            var relative = RelativePath(f, root);
            ScanFile(f, relative, graph);
        }

        await _buildLock.WaitAsync(ct);
        try { await _store.SaveAsync(graph, ct); }
        finally { _buildLock.Release(); }

        return (graph.Nodes.Count, graph.Edges.Count);
    }

    /// <summary>
    /// Upserts an <see cref="AdrEntry"/> as a graph node and wires <see cref="EdgeType.AdrGoverns"/>
    /// edges to every file or symbol listed in <paramref name="adr"/>.<c>Governs</c>.
    /// </summary>
    public async Task UpsertAdrNodeAsync(AdrEntry adr, CancellationToken ct = default)
    {
        await _buildLock.WaitAsync(ct);
        try
        {
            var graph  = await _store.LoadAsync(ct);
            var adrId  = $"adr:{adr.Id}";

            // Remove stale ADR node and its outgoing adr_governs edges.
            graph.Nodes.RemoveAll(n => string.Equals(n.Id, adrId, StringComparison.Ordinal));
            graph.Edges.RemoveAll(e =>
                string.Equals(e.From, adrId, StringComparison.Ordinal) &&
                string.Equals(e.Relation, EdgeType.AdrGoverns, StringComparison.Ordinal));

            graph.AddNode(new RepositoryGraphNode
            {
                Id        = adrId,
                Kind      = NodeType.Adr,
                Name      = adr.Id,
                Timestamp = DateTimeOffset.UtcNow,
            });

            foreach (var governed in adr.Governs)
            {
                var target = NormalizeGovernsTarget(governed, graph);
                if (target is null) continue;
                graph.AddEdge(new RepositoryGraphEdge
                {
                    From     = adrId,
                    To       = target,
                    Relation = EdgeType.AdrGoverns,
                });
            }

            await _store.SaveAsync(graph, ct);
        }
        finally { _buildLock.Release(); }
    }

    // ── File scanning ─────────────────────────────────────────────────────────

    private void ScanFile(string absolutePath, string relativePath, RepositoryGraph graph)
    {
        string[] lines;
        try { lines = File.ReadAllLines(absolutePath); }
        catch { return; }

        // File node
        var fileId = $"file:{relativePath}";
        graph.AddNode(new RepositoryGraphNode
        {
            Id       = fileId,
            Kind     = NodeType.File,
            FilePath = relativePath,
            Name     = Path.GetFileName(relativePath),
        });

        string? currentNamespace = null;
        string? currentType      = null;
        NodeType currentKind     = NodeType.Type;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNo = i + 1;

            // Namespace declaration
            var nsMatch = NamespaceRx.Match(line);
            if (nsMatch.Success)
            {
                currentNamespace = nsMatch.Groups[1].Value;
                var nsId = $"namespace:{currentNamespace}";
                graph.AddNode(new RepositoryGraphNode
                {
                    Id        = nsId,
                    Kind      = NodeType.Namespace,
                    FilePath  = relativePath,
                    Name      = currentNamespace,
                    StartLine = lineNo,
                });
                graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = nsId, Relation = EdgeType.Defines });
                continue;
            }

            // Using directives
            var usingMatch = UsingRx.Match(line);
            if (usingMatch.Success && !line.Contains("="))
            {
                var imported = usingMatch.Groups[1].Value;
                var importId = $"namespace:{imported}";
                if (graph.FindById(importId) is null)
                    graph.AddNode(new RepositoryGraphNode { Id = importId, Kind = NodeType.Namespace, Name = imported });
                graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = importId, Relation = EdgeType.Imports });
                continue;
            }

            // Interface declaration
            var ifaceMatch = InterfaceRx.Match(line);
            if (ifaceMatch.Success)
            {
                var name  = ifaceMatch.Groups[1].Value;
                var fqn   = currentNamespace is not null ? $"{currentNamespace}.{name}" : name;
                var id    = $"interface:{fqn}";
                graph.AddNode(new RepositoryGraphNode
                {
                    Id        = id,
                    Kind      = NodeType.Interface,
                    FilePath  = relativePath,
                    Name      = name,
                    Namespace = currentNamespace,
                    StartLine = lineNo,
                });
                graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = id, Relation = EdgeType.Defines });
                currentType = fqn;
                currentKind = NodeType.Interface;

                AddInheritanceEdges(id, ifaceMatch.Groups[2].Value, currentNamespace, NodeType.Interface, graph);
                continue;
            }

            // Record declaration (before class so "record class" is caught here)
            var recMatch = RecordRx.Match(line);
            if (recMatch.Success)
            {
                var name  = recMatch.Groups[1].Value;
                var fqn   = currentNamespace is not null ? $"{currentNamespace}.{name}" : name;
                var id    = $"type:{fqn}";
                graph.AddNode(new RepositoryGraphNode
                {
                    Id        = id,
                    Kind      = NodeType.Type,
                    FilePath  = relativePath,
                    Name      = name,
                    Namespace = currentNamespace,
                    StartLine = lineNo,
                });
                graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = id, Relation = EdgeType.Defines });
                currentType = fqn;
                currentKind = NodeType.Type;

                AddInheritanceEdges(id, recMatch.Groups[2].Value, currentNamespace, NodeType.Type, graph);
                continue;
            }

            // Class declaration
            var classMatch = ClassRx.Match(line);
            if (classMatch.Success)
            {
                var name  = classMatch.Groups[1].Value;
                var fqn   = currentNamespace is not null ? $"{currentNamespace}.{name}" : name;
                var id    = $"type:{fqn}";
                graph.AddNode(new RepositoryGraphNode
                {
                    Id        = id,
                    Kind      = NodeType.Type,
                    FilePath  = relativePath,
                    Name      = name,
                    Namespace = currentNamespace,
                    StartLine = lineNo,
                });
                graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = id, Relation = EdgeType.Defines });
                currentType = fqn;
                currentKind = NodeType.Type;

                AddInheritanceEdges(id, classMatch.Groups[2].Value, currentNamespace, NodeType.Type, graph);
                continue;
            }

            if (currentType is null) continue;
            var typeId = $"{(currentKind == NodeType.Interface ? "interface" : "type")}:{currentType}";

            // Method declaration (coarse heuristic — skip property accessors)
            if (!line.TrimStart().StartsWith("get") && !line.TrimStart().StartsWith("set") &&
                !line.TrimStart().StartsWith("init") && !line.TrimStart().StartsWith("//"))
            {
                var methMatch = MethodRx.Match(line);
                if (methMatch.Success)
                {
                    var name  = methMatch.Groups[1].Value;
                    if (!IsKeyword(name))
                    {
                        var id = $"method:{currentType}.{name}";
                        graph.AddNode(new RepositoryGraphNode
                        {
                            Id        = id,
                            Kind      = NodeType.Method,
                            FilePath  = relativePath,
                            Name      = name,
                            Namespace = currentNamespace,
                            StartLine = lineNo,
                        });
                        graph.AddEdge(new RepositoryGraphEdge { From = typeId, To = id, Relation = EdgeType.Defines });
                        continue;
                    }
                }
            }

            // Property declaration
            var propMatch = PropertyRx.Match(line);
            if (propMatch.Success)
            {
                var name = propMatch.Groups[1].Value;
                if (!IsKeyword(name))
                {
                    var id = $"property:{currentType}.{name}";
                    graph.AddNode(new RepositoryGraphNode
                    {
                        Id        = id,
                        Kind      = NodeType.Property,
                        FilePath  = relativePath,
                        Name      = name,
                        Namespace = currentNamespace,
                        StartLine = lineNo,
                    });
                    graph.AddEdge(new RepositoryGraphEdge { From = typeId, To = id, Relation = EdgeType.Defines });
                    continue;
                }
            }

            // Field declaration
            var fieldMatch = FieldRx.Match(line);
            if (fieldMatch.Success)
            {
                var name = fieldMatch.Groups[1].Value;
                if (!IsKeyword(name))
                {
                    var id = $"field:{currentType}.{name}";
                    graph.AddNode(new RepositoryGraphNode
                    {
                        Id        = id,
                        Kind      = NodeType.Field,
                        FilePath  = relativePath,
                        Name      = name,
                        Namespace = currentNamespace,
                        StartLine = lineNo,
                    });
                    graph.AddEdge(new RepositoryGraphEdge { From = typeId, To = id, Relation = EdgeType.Defines });
                }
            }
        }
    }

    private static void AddInheritanceEdges(
        string fromId,
        string baseListRaw,
        string? currentNamespace,
        NodeType fromKind,
        RepositoryGraph graph)
    {
        if (string.IsNullOrWhiteSpace(baseListRaw)) return;

        foreach (var raw in baseListRaw.Split(','))
        {
            var name = raw.Trim().Split('<')[0].Trim(); // strip generic args
            if (string.IsNullOrEmpty(name)) continue;

            // Heuristic: interfaces start with I followed by uppercase
            bool looksLikeInterface = name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);
            var  prefix = looksLikeInterface ? "interface" : "type";
            var  toId   = currentNamespace is not null ? $"{prefix}:{currentNamespace}.{name}" : $"{prefix}:{name}";

            // Ensure target node exists (as a stub) so edges are valid.
            if (graph.FindById(toId) is null)
                graph.AddNode(new RepositoryGraphNode
                {
                    Id   = toId,
                    Kind = looksLikeInterface ? NodeType.Interface : NodeType.Type,
                    Name = name,
                    Namespace = currentNamespace,
                });

            var relation = looksLikeInterface ? EdgeType.Implements : EdgeType.Inherits;
            graph.AddEdge(new RepositoryGraphEdge { From = fromId, To = toId, Relation = relation });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string RelativePath(string absolute, string? root = null)
    {
        var baseDir = root ?? _projectRoot;
        try
        {
            var rel = Path.GetRelativePath(baseDir, absolute);
            return rel.Replace('\\', '/');
        }
        catch { return Path.GetFileName(absolute); }
    }

    private static string? NormalizeGovernsTarget(string governed, RepositoryGraph graph)
    {
        // Already a SymbolId — verify it exists or return as-is.
        if (governed.Contains(':'))
        {
            var node = graph.FindById(governed);
            return node is not null ? governed : governed; // accept even if not yet in graph
        }

        // Looks like a file path — normalise separators and look for a file node.
        var normalised = governed.Replace('\\', '/');
        var fileId     = $"file:{normalised}";
        return fileId;
    }

    private static bool IsKeyword(string name) =>
        name is "if" or "else" or "while" or "for" or "foreach" or "switch" or "case"
              or "return" or "throw" or "catch" or "finally" or "try" or "new" or "this"
              or "base" or "null" or "true" or "false" or "var" or "void" or "override"
              or "virtual" or "abstract" or "sealed" or "static" or "readonly" or "const";

    private static bool IsBuildArtifact(string path) =>
        path.Contains("/obj/", StringComparison.Ordinal) ||
        path.Contains("\\obj\\", StringComparison.Ordinal) ||
        path.Contains("/bin/", StringComparison.Ordinal) ||
        path.Contains("\\bin\\", StringComparison.Ordinal);
}
