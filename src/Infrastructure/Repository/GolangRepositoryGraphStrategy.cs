using System.Text.RegularExpressions;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Repository;

/// <summary>
/// <see cref="IRepositoryGraphStrategy"/> for Go source.
///
/// <para>
/// Uses structural text analysis (regex over source lines) to extract symbol declarations —
/// no Go AST/parser dependency required. Each file is scanned in isolation so incremental
/// rebuilds update only the nodes in the changed file. Assumes gofmt-formatted input (opening
/// braces on the declaration line, one declaration per line) — the same "reasonably formatted
/// source" assumption <see cref="DotNetRepositoryGraphStrategy"/> makes for C#.
/// </para>
///
/// <para>
/// SymbolId scheme (stable, fully-qualified names):
/// <list type="bullet">
///   <item><c>file:relative/path/to/file.go</c></item>
///   <item><c>package:packageName</c> — keyed by the declared <c>package</c> name (not the
///     directory/import path), matching how <c>namespace:</c> works for C#. Go re-declares the
///     package name in every file of that package; re-adding the same node across files is a
///     no-op since <see cref="RepositoryGraph.AddNode"/> is idempotent by Id.</item>
///   <item><c>type:packageName.StructName</c></item>
///   <item><c>interface:packageName.InterfaceName</c></item>
///   <item><c>method:packageName.ReceiverType.MethodName</c> — receiver methods.</item>
///   <item><c>method:packageName.FunctionName</c> — package-level ("free") functions, since Go
///     has no enclosing type for these; the package itself is the owner and the
///     <see cref="EdgeType.Defines"/> edge runs from the <c>package:</c> node.</item>
///   <item><c>field:packageName.StructName.FieldName</c></item>
/// </list>
/// </para>
///
/// <para>
/// Design notes:
/// <list type="bullet">
///   <item>
///     Exported vs. unexported (capitalized vs. lowercase identifiers) is not used to gate
///     node creation — both are indexed identically. Go has no <c>public</c>/<c>private</c>
///     keywords; visibility is purely a naming convention with no structural signal to key off.
///   </item>
///   <item>
///     Embedded fields (Go's composition mechanism) are Go's rough analog to inheritance and
///     reuse <see cref="EdgeType.Inherits"/>/<see cref="EdgeType.Implements"/> rather than a new
///     "embeds" relation, mirroring <c>AddInheritanceEdges</c> in the C# strategy. Inside an
///     interface body, an embedded name is always another interface (the Go spec forbids struct
///     embedding there), so that case is resolved deterministically as
///     <see cref="EdgeType.Implements"/>. Inside a struct body it's ambiguous — the embedded
///     name could be a struct or an interface — so it's resolved by first checking whether a
///     matching <c>interface:</c> or <c>type:</c> node is already known, and otherwise falling
///     back to a naming heuristic (interface names conventionally end in "-er"/"-or": Reader,
///     Writer, Formatter, Visitor), the same best-effort-naming-convention approach the C#
///     strategy uses for its own base-list resolution.
///   </item>
///   <item>
///     Local type declarations and function literals nested inside a function body are not
///     distinguished from package-level declarations (this scanner does not track function-body
///     nesting, only struct/interface body nesting) — a function-local <c>type Foo struct {...}</c>
///     would be misattributed as a package-level type. This mirrors the coarse-heuristic
///     trade-offs already accepted in <see cref="DotNetRepositoryGraphStrategy"/>.
///   </item>
///   <item>
///     Import target nodes are named by their declared alias, or by the last path segment of the
///     import path when unaliased (e.g. <c>"net/http"</c> → <c>http</c>). This is a heuristic —
///     Go does not guarantee the package name matches the last path segment — but it converges
///     with the real <c>package:</c> node once that package's own files are scanned, since
///     <see cref="RepositoryGraph.AddNode"/> merges by Id.
///   </item>
///   <item>
///     Multiple field names sharing one type on a single line (<c>X, Y int</c>) and single-line
///     struct/interface bodies (<c>type P struct{ X, Y int }</c>) are not parsed field-by-field;
///     only the type node itself is still recorded. Real-world gofmt output almost always spreads
///     struct bodies across multiple lines, so this is a narrow, accepted gap.
///   </item>
/// </list>
/// </para>
/// </summary>
public sealed class GolangRepositoryGraphStrategy : IRepositoryGraphStrategy
{
    // Structural patterns for Go source
    private static readonly Regex PackageRx           = new(@"^\s*package\s+(\w+)", RegexOptions.Compiled);
    private static readonly Regex ImportBlockStartRx  = new(@"^\s*import\s*\(\s*$", RegexOptions.Compiled);
    private static readonly Regex ImportSingleRx      = new(@"^\s*import\s+(?:(\w+|_|\.)\s+)?""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex ImportEntryRx        = new(@"^\s*(?:(\w+|_|\.)\s+)?""([^""]+)""\s*$", RegexOptions.Compiled);
    private static readonly Regex StructRx            = new(@"^\s*type\s+(\w+)(?:\[[^\]]*\])?\s+struct\s*\{", RegexOptions.Compiled);
    private static readonly Regex InterfaceRx         = new(@"^\s*type\s+(\w+)(?:\[[^\]]*\])?\s+interface\s*\{", RegexOptions.Compiled);
    private static readonly Regex ReceiverMethodRx    = new(@"^\s*func\s*\(\s*\w+\s+(\*)?(\w+)(?:\[[^\]]*\])?\s*\)\s+(\w+)\s*(?:\[[^\]]*\])?\s*\(", RegexOptions.Compiled);
    private static readonly Regex FreeFunctionRx      = new(@"^\s*func\s+(\w+)\s*(?:\[[^\]]*\])?\s*\(", RegexOptions.Compiled);
    private static readonly Regex NamedFieldRx        = new(@"^\s*([A-Za-z_]\w*)\s+\S.*$", RegexOptions.Compiled);
    private static readonly Regex EmbeddedFieldRx     = new(@"^\s*\*?([A-Za-z_][\w.]*)\s*(?:`[^`]*`)?\s*$", RegexOptions.Compiled);

    public IReadOnlyList<string> FileGlobs { get; } = ["*.go"];

    public bool CanHandle(string absoluteFilePath) =>
        absoluteFilePath.EndsWith(".go", StringComparison.OrdinalIgnoreCase);

    public void ScanFile(string absolutePath, string relativePath, RepositoryGraph graph)
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

        string? currentPackage    = null;
        string? currentType       = null; // fully-qualified "pkg.Name" of the struct/interface body we're inside
        NodeType currentKind      = NodeType.Type;
        int typeBraceDepth        = 0;
        bool insideImportBlock    = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var lineNo  = i + 1;
            var line    = StripLineComment(lines[i]);
            var trimmed = line.Trim();

            // ── Grouped import block ────────────────────────────────────────
            if (insideImportBlock)
            {
                if (trimmed == ")") { insideImportBlock = false; continue; }
                var entryMatch = ImportEntryRx.Match(line);
                if (entryMatch.Success)
                    AddImportEdge(fileId, entryMatch.Groups[1].Value, entryMatch.Groups[2].Value, graph);
                continue;
            }

            if (trimmed.Length == 0) continue;

            // ── Inside a struct/interface body: only fields/embeds and close detection ──
            if (currentType is not null)
            {
                if (trimmed == "}")
                {
                    currentType = null;
                    continue;
                }

                var net = NetBraces(line);
                if (typeBraceDepth + net <= 0)
                {
                    currentType = null;
                    continue;
                }
                typeBraceDepth += net;

                var typeId = currentKind == NodeType.Interface ? $"interface:{currentType}" : $"type:{currentType}";

                var embMatch = EmbeddedFieldRx.Match(line);
                if (embMatch.Success)
                {
                    AddEmbeddedEdge(typeId, embMatch.Groups[1].Value, currentPackage ?? "_", currentKind, graph);
                    continue;
                }

                if (currentKind == NodeType.Type)
                {
                    var fieldMatch = NamedFieldRx.Match(line);
                    if (fieldMatch.Success)
                    {
                        var name = fieldMatch.Groups[1].Value;
                        var id   = $"field:{currentType}.{name}";
                        graph.AddNode(new RepositoryGraphNode
                        {
                            Id        = id,
                            Kind      = NodeType.Field,
                            FilePath  = relativePath,
                            Name      = name,
                            Namespace = currentPackage,
                            StartLine = lineNo,
                        });
                        graph.AddEdge(new RepositoryGraphEdge { From = typeId, To = id, Relation = EdgeType.Defines });
                    }
                }
                continue;
            }

            // ── Package declaration ─────────────────────────────────────────
            var pkgMatch = PackageRx.Match(line);
            if (pkgMatch.Success)
            {
                currentPackage = pkgMatch.Groups[1].Value;
                var pkgId = $"package:{currentPackage}";
                graph.AddNode(new RepositoryGraphNode
                {
                    Id        = pkgId,
                    Kind      = NodeType.Package,
                    FilePath  = relativePath,
                    Name      = currentPackage,
                    StartLine = lineNo,
                });
                graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = pkgId, Relation = EdgeType.Defines });
                continue;
            }

            // ── Imports ──────────────────────────────────────────────────────
            if (ImportBlockStartRx.IsMatch(line))
            {
                insideImportBlock = true;
                continue;
            }

            var impMatch = ImportSingleRx.Match(line);
            if (impMatch.Success)
            {
                AddImportEdge(fileId, impMatch.Groups[1].Value, impMatch.Groups[2].Value, graph);
                continue;
            }

            var pkg = currentPackage ?? "_";

            // ── Interface declaration ───────────────────────────────────────
            var ifaceMatch = InterfaceRx.Match(line);
            if (ifaceMatch.Success)
            {
                var name = ifaceMatch.Groups[1].Value;
                var fqn  = $"{pkg}.{name}";
                var id   = $"interface:{fqn}";
                graph.AddNode(new RepositoryGraphNode
                {
                    Id        = id,
                    Kind      = NodeType.Interface,
                    FilePath  = relativePath,
                    Name      = name,
                    Namespace = pkg,
                    StartLine = lineNo,
                });
                graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = id, Relation = EdgeType.Defines });

                var net = NetBraces(line);
                if (net > 0)
                {
                    currentType    = fqn;
                    currentKind    = NodeType.Interface;
                    typeBraceDepth = net;
                }
                continue;
            }

            // ── Struct declaration ───────────────────────────────────────────
            var structMatch = StructRx.Match(line);
            if (structMatch.Success)
            {
                var name = structMatch.Groups[1].Value;
                var fqn  = $"{pkg}.{name}";
                var id   = $"type:{fqn}";
                graph.AddNode(new RepositoryGraphNode
                {
                    Id        = id,
                    Kind      = NodeType.Type,
                    FilePath  = relativePath,
                    Name      = name,
                    Namespace = pkg,
                    StartLine = lineNo,
                });
                graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = id, Relation = EdgeType.Defines });

                var net = NetBraces(line);
                if (net > 0)
                {
                    currentType    = fqn;
                    currentKind    = NodeType.Type;
                    typeBraceDepth = net;
                }
                continue;
            }

            // ── Receiver method ──────────────────────────────────────────────
            var recvMatch = ReceiverMethodRx.Match(line);
            if (recvMatch.Success)
            {
                var receiverType = recvMatch.Groups[2].Value;
                var methodName   = recvMatch.Groups[3].Value;
                var receiverFqn  = $"{pkg}.{receiverType}";
                var receiverId   = $"type:{receiverFqn}";

                if (graph.FindById(receiverId) is null)
                    graph.AddNode(new RepositoryGraphNode
                    {
                        Id        = receiverId,
                        Kind      = NodeType.Type,
                        Name      = receiverType,
                        Namespace = pkg,
                    });

                var id = $"method:{receiverFqn}.{methodName}";
                graph.AddNode(new RepositoryGraphNode
                {
                    Id        = id,
                    Kind      = NodeType.Method,
                    FilePath  = relativePath,
                    Name      = methodName,
                    Namespace = pkg,
                    StartLine = lineNo,
                });
                graph.AddEdge(new RepositoryGraphEdge { From = receiverId, To = id, Relation = EdgeType.Defines });
                continue;
            }

            // ── Free (package-level) function ────────────────────────────────
            var funcMatch = FreeFunctionRx.Match(line);
            if (funcMatch.Success)
            {
                var name  = funcMatch.Groups[1].Value;
                var pkgId = $"package:{pkg}";
                if (graph.FindById(pkgId) is null)
                    graph.AddNode(new RepositoryGraphNode { Id = pkgId, Kind = NodeType.Package, Name = pkg });

                var id = $"method:{pkg}.{name}";
                graph.AddNode(new RepositoryGraphNode
                {
                    Id        = id,
                    Kind      = NodeType.Method,
                    FilePath  = relativePath,
                    Name      = name,
                    Namespace = pkg,
                    StartLine = lineNo,
                });
                graph.AddEdge(new RepositoryGraphEdge { From = pkgId, To = id, Relation = EdgeType.Defines });
            }
        }
    }

    private static void AddImportEdge(string fileId, string alias, string importPath, RepositoryGraph graph)
    {
        var lastSegment = importPath.Split('/')[^1];
        var name = !string.IsNullOrEmpty(alias) && alias is not "_" and not "."
            ? alias
            : lastSegment;

        var importId = $"package:{name}";
        if (graph.FindById(importId) is null)
            graph.AddNode(new RepositoryGraphNode { Id = importId, Kind = NodeType.Package, Name = name });

        graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = importId, Relation = EdgeType.Imports });
    }

    private static void AddEmbeddedEdge(
        string fromTypeId,
        string embeddedNameRaw,
        string currentPackage,
        NodeType enclosingKind,
        RepositoryGraph graph)
    {
        if (string.IsNullOrEmpty(embeddedNameRaw)) return;

        string fqn, simpleName;
        var dotIndex = embeddedNameRaw.IndexOf('.');
        if (dotIndex >= 0)
        {
            fqn        = embeddedNameRaw;
            simpleName = embeddedNameRaw[(dotIndex + 1)..];
        }
        else
        {
            fqn        = $"{currentPackage}.{embeddedNameRaw}";
            simpleName = embeddedNameRaw;
        }

        NodeType targetKind;
        if (enclosingKind == NodeType.Interface)
        {
            // Go spec: interface bodies may only embed other interfaces.
            targetKind = NodeType.Interface;
        }
        else if (graph.FindById($"interface:{fqn}") is not null)
        {
            targetKind = NodeType.Interface;
        }
        else if (graph.FindById($"type:{fqn}") is not null)
        {
            targetKind = NodeType.Type;
        }
        else
        {
            // Heuristic fallback: Go interfaces conventionally end in "-er"/"-or"
            // (Reader, Writer, Formatter, Visitor); anything else is assumed to be
            // struct composition, the more common use of embedding.
            targetKind = simpleName.EndsWith("er", StringComparison.Ordinal) ||
                         simpleName.EndsWith("or", StringComparison.Ordinal)
                ? NodeType.Interface
                : NodeType.Type;
        }

        var prefix = targetKind == NodeType.Interface ? "interface" : "type";
        var toId   = $"{prefix}:{fqn}";

        if (graph.FindById(toId) is null)
            graph.AddNode(new RepositoryGraphNode { Id = toId, Kind = targetKind, Name = simpleName });

        var relation = targetKind == NodeType.Interface ? EdgeType.Implements : EdgeType.Inherits;
        graph.AddEdge(new RepositoryGraphEdge { From = fromTypeId, To = toId, Relation = relation });
    }

    private static string StripLineComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static int NetBraces(string line)
    {
        var net = 0;
        foreach (var c in line)
        {
            if (c == '{') net++;
            else if (c == '}') net--;
        }
        return net;
    }
}
