using System.Text;
using System.Text.RegularExpressions;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Repository;

/// <summary>
/// <see cref="IRepositoryGraphStrategy"/> for Python source.
///
/// <para>
/// Uses structural text analysis (regex over source lines) to extract symbol declarations —
/// no Python AST dependency required. Each file is scanned in isolation so incremental rebuilds
/// update only the nodes in the changed file. Python has no braces, so scope (which class or
/// function body a line belongs to) is tracked by indentation depth rather than the brace-depth
/// counter <see cref="GolangRepositoryGraphStrategy"/> uses — a stack of (kind, fully-qualified
/// name, header indent) entries, popped whenever a line's indent drops to or below an entry's
/// header indent.
/// </para>
///
/// <para>
/// SymbolId scheme (stable, fully-qualified names):
/// <list type="bullet">
///   <item><c>file:relative/path/to/file.py</c></item>
///   <item><c>package:dotted.module.path</c> — one per file, derived from
///     <paramref name="relativePath"/>: slashes become dots and the <c>.py</c> extension is
///     stripped (e.g. <c>foo/bar.py</c> → <c>foo.bar</c>). For <c>__init__.py</c> the trailing
///     <c>__init__</c> segment is dropped, so the package's identity is its directory
///     (<c>foo/__init__.py</c> → <c>foo</c>) — matching Python's own import semantics, where
///     <c>import foo</c> resolves to the package, not <c>foo.__init__</c>.</item>
///   <item><c>type:dotted.module.path.ClassName</c> (nested classes append further segments,
///     e.g. <c>type:pkg.mod.Outer.Inner</c>).</item>
///   <item><c>method:dotted.module.path.FunctionName</c> — module-level ("free") functions,
///     owned by the <c>package:</c> node since Python has no enclosing type for these.</item>
///   <item><c>method:dotted.module.path.ClassName.method_name</c> — methods, owned by their
///     class.</item>
///   <item><c>field:dotted.module.path.ClassName.attr_name</c> — class-level attributes
///     (annotated or assigned directly in the class body).</item>
/// </list>
/// </para>
///
/// <para>
/// Design notes:
/// <list type="bullet">
///   <item>
///     Python has no formal interface keyword, so unlike C#/Go this strategy never produces
///     <see cref="NodeType.Interface"/> or <see cref="EdgeType.Implements"/> — every base-class
///     reference (however abstract) becomes <see cref="EdgeType.Inherits"/>. There's no reliable
///     syntactic signal (naming convention or otherwise) to key an Implements distinction off.
///   </item>
///   <item>
///     An unqualified base class name (<c>class Dog(Animal):</c>) is assumed to live in the
///     current module, exactly like <see cref="DotNetRepositoryGraphStrategy"/> assumes an
///     unqualified base type lives in the current namespace. This is frequently wrong for Python
///     specifically (bases are commonly imported from elsewhere via
///     <c>from other import Base</c>), but resolving it properly would require cross-referencing
///     each file's own import aliases — an enhancement intentionally left out to match the
///     existing, accepted C# limitation rather than hold Python to a higher bar.
///   </item>
///   <item>
///     Module-level variables/constants are intentionally not indexed as nodes (no Go-strategy
///     equivalent exists for package-level <c>var</c>/<c>const</c> either) — only class-level
///     attributes are recorded as <see cref="NodeType.Field"/>. This keeps the two strategies'
///     scope parallel and avoids the much higher false-positive rate of matching arbitrary
///     top-level statements (argparse setup, <c>if __name__ == "__main__":</c> blocks, etc.).
///   </item>
///   <item>
///     Function/class declarations nested inside a function body (not a class body) are not
///     distinguished from module-level declarations — this scanner only tracks class-body
///     nesting for attribution purposes. Mirrors the accepted "local type in a function body"
///     gap documented on <see cref="GolangRepositoryGraphStrategy"/>.
///   </item>
///   <item>
///     Multi-line <c>class Foo(\n Base1,\n Base2,\n):</c> declarations (parenthesized base list
///     spanning several lines, as `black`-formatted code commonly produces) are supported via
///     simple paren-balance accumulation, the same "track one piece of block state" approach
///     Go's grouped-import-block handling uses. Multi-line <c>def foo(\n a,\n b,\n):</c>
///     signatures need no special handling at all — the declaration is recognized from its
///     opening paren alone, and interior parameter lines are silently ignored because scope
///     tracking prevents them from being misread as class attributes.
///   </item>
///   <item>
///     <c>from X import Y</c> only records an edge to module <c>X</c> — <c>Y</c> is not modeled
///     as a separate symbol (it may be a submodule or a name, and disambiguating requires
///     resolving X on disk). This mirrors Go/C# not modeling individual imported members beyond
///     the containing package/namespace.
///   </item>
/// </list>
/// </para>
/// </summary>
public sealed class PythonRepositoryGraphStrategy : IRepositoryGraphStrategy
{
    private enum ScopeKind { Class, Def }

    // Structural patterns for Python source
    private static readonly Regex FromImportRx     = new(@"^\s*from\s+(\.*)([\w.]*)\s+import\b", RegexOptions.Compiled);
    private static readonly Regex ImportStmtRx     = new(@"^\s*import\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex ImportEntryRx    = new(@"^([\w.]+)(?:\s+as\s+(\w+))?$", RegexOptions.Compiled);
    private static readonly Regex ClassRx          = new(@"^\s*class\s+(\w+)(?:\s*\[[^\]]*\])?\s*(?:\(([^()]*)\))?\s*:", RegexOptions.Compiled);
    private static readonly Regex ClassHeaderOpenRx = new(@"^\s*class\s+\w+", RegexOptions.Compiled);
    private static readonly Regex DefRx            = new(@"^\s*(?:async\s+)?def\s+(\w+)\s*(?:\[[^\]]*\])?\s*\(", RegexOptions.Compiled);
    private static readonly Regex FieldAnnotatedRx = new(@"^\s*([A-Za-z_]\w*)\s*:\s*[^=\s].*$", RegexOptions.Compiled);
    private static readonly Regex FieldAssignRx    = new(@"^\s*([A-Za-z_]\w*)\s*=(?!=)\s*\S.*$", RegexOptions.Compiled);

    public IReadOnlyList<string> FileGlobs { get; } = ["*.py"];

    public bool CanHandle(string absoluteFilePath) =>
        absoluteFilePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

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

        // Module (package) node — Python has no explicit declaration for this; identity is
        // derived from the file's own path.
        var moduleDotted = ModulePathFor(relativePath);
        if (string.IsNullOrEmpty(moduleDotted)) moduleDotted = "_";
        var moduleId = $"package:{moduleDotted}";
        graph.AddNode(new RepositoryGraphNode
        {
            Id       = moduleId,
            Kind     = NodeType.Package,
            FilePath = relativePath,
            Name     = moduleDotted,
        });
        graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = moduleId, Relation = EdgeType.Defines });

        var dirDotted = DirDottedPath(relativePath);
        var scopes    = new List<(ScopeKind Kind, string Fqn, int Indent)>();

        var collectingClassHeader = false;
        var classHeaderBuffer     = new StringBuilder();
        var classHeaderIndent     = 0;
        var classHeaderLineNo     = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var lineNo  = i + 1;
            var line    = StripLineComment(lines[i]);
            var trimmed = line.Trim();

            // ── Multi-line class header (unclosed base-list parens) ─────────
            if (collectingClassHeader)
            {
                classHeaderBuffer.Append(' ').Append(trimmed);
                var buffered = classHeaderBuffer.ToString();
                if (NetParens(buffered) <= 0 && buffered.TrimEnd().EndsWith(':'))
                {
                    var match = ClassRx.Match(buffered);
                    if (match.Success)
                        HandleClass(match, classHeaderIndent, classHeaderLineNo);
                    collectingClassHeader = false;
                }
                continue;
            }

            if (trimmed.Length == 0) continue;

            var indent = IndentOf(line);
            while (scopes.Count > 0 && indent <= scopes[^1].Indent)
                scopes.RemoveAt(scopes.Count - 1);

            // ── Imports (recorded regardless of nesting) ─────────────────────
            var fromMatch = FromImportRx.Match(line);
            if (fromMatch.Success)
            {
                var dots         = fromMatch.Groups[1].Value;
                var moduleSuffix = fromMatch.Groups[2].Value;
                var target = dots.Length > 0
                    ? ResolveRelativeModule(dirDotted, dots.Length, moduleSuffix)
                    : moduleSuffix;
                AddImportEdge(fileId, target, graph);
                continue;
            }

            var importMatch = ImportStmtRx.Match(line);
            if (importMatch.Success)
            {
                foreach (var rawEntry in importMatch.Groups[1].Value.Split(','))
                {
                    var entryMatch = ImportEntryRx.Match(rawEntry.Trim());
                    if (entryMatch.Success)
                        AddImportEdge(fileId, entryMatch.Groups[1].Value, graph);
                }
                continue;
            }

            // ── Class declaration ─────────────────────────────────────────────
            var classMatch = ClassRx.Match(line);
            if (classMatch.Success)
            {
                HandleClass(classMatch, indent, lineNo);
                continue;
            }
            if (ClassHeaderOpenRx.IsMatch(line) && NetParens(line) > 0)
            {
                collectingClassHeader = true;
                classHeaderBuffer.Clear().Append(trimmed);
                classHeaderIndent = indent;
                classHeaderLineNo = lineNo;
                continue;
            }

            // ── Function / method declaration ─────────────────────────────────
            var defMatch = DefRx.Match(line);
            if (defMatch.Success)
            {
                var name = defMatch.Groups[1].Value;
                var parentClass = scopes.Count > 0 && scopes[^1].Kind == ScopeKind.Class ? scopes[^1].Fqn : null;

                string methodFqn, ownerId;
                if (parentClass is not null)
                {
                    methodFqn = $"{parentClass}.{name}";
                    ownerId   = $"type:{parentClass}";
                }
                else
                {
                    methodFqn = $"{moduleDotted}.{name}";
                    ownerId   = moduleId;
                }

                var id = $"method:{methodFqn}";
                graph.AddNode(new RepositoryGraphNode
                {
                    Id        = id,
                    Kind      = NodeType.Method,
                    FilePath  = relativePath,
                    Name      = name,
                    Namespace = moduleDotted,
                    StartLine = lineNo,
                });
                graph.AddEdge(new RepositoryGraphEdge { From = ownerId, To = id, Relation = EdgeType.Defines });

                scopes.Add((ScopeKind.Def, methodFqn, indent));
                continue;
            }

            // ── Class-level attribute (field) ─────────────────────────────────
            if (scopes.Count > 0 && scopes[^1].Kind == ScopeKind.Class)
            {
                var ownerFqn = scopes[^1].Fqn;
                var fieldMatch = FieldAnnotatedRx.Match(line);
                if (!fieldMatch.Success) fieldMatch = FieldAssignRx.Match(line);
                if (fieldMatch.Success)
                {
                    var name = fieldMatch.Groups[1].Value;
                    var id   = $"field:{ownerFqn}.{name}";
                    graph.AddNode(new RepositoryGraphNode
                    {
                        Id        = id,
                        Kind      = NodeType.Field,
                        FilePath  = relativePath,
                        Name      = name,
                        Namespace = moduleDotted,
                        StartLine = lineNo,
                    });
                    graph.AddEdge(new RepositoryGraphEdge { From = $"type:{ownerFqn}", To = id, Relation = EdgeType.Defines });
                }
            }
        }

        void HandleClass(Match match, int indent, int lineNo)
        {
            var name        = match.Groups[1].Value;
            var parentClass = scopes.Count > 0 && scopes[^1].Kind == ScopeKind.Class ? scopes[^1].Fqn : null;
            var fqn         = parentClass is not null ? $"{parentClass}.{name}" : $"{moduleDotted}.{name}";
            var id          = $"type:{fqn}";

            graph.AddNode(new RepositoryGraphNode
            {
                Id        = id,
                Kind      = NodeType.Type,
                FilePath  = relativePath,
                Name      = name,
                Namespace = moduleDotted,
                StartLine = lineNo,
            });
            graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = id, Relation = EdgeType.Defines });

            AddBaseClassEdges(id, match.Groups[2].Value, moduleDotted, graph);

            scopes.Add((ScopeKind.Class, fqn, indent));
        }
    }

    private static void AddImportEdge(string fileId, string targetModule, RepositoryGraph graph)
    {
        if (string.IsNullOrEmpty(targetModule)) return;

        var importId = $"package:{targetModule}";
        if (graph.FindById(importId) is null)
            graph.AddNode(new RepositoryGraphNode { Id = importId, Kind = NodeType.Package, Name = targetModule });

        graph.AddEdge(new RepositoryGraphEdge { From = fileId, To = importId, Relation = EdgeType.Imports });
    }

    private static void AddBaseClassEdges(string fromTypeId, string baseListRaw, string moduleDotted, RepositoryGraph graph)
    {
        if (string.IsNullOrWhiteSpace(baseListRaw)) return;

        foreach (var raw in baseListRaw.Split(','))
        {
            var entry = raw.Trim();
            if (entry.Length == 0 || entry.Contains('=')) continue; // skip keyword args, e.g. metaclass=Meta

            var name = entry.Split('[')[0].Trim(); // strip generic subscript, e.g. Generic[T]
            if (name.Length == 0 || name == "object") continue;

            var fqn  = name.Contains('.') ? name : $"{moduleDotted}.{name}";
            var toId = $"type:{fqn}";

            if (graph.FindById(toId) is null)
                graph.AddNode(new RepositoryGraphNode
                {
                    Id   = toId,
                    Kind = NodeType.Type,
                    Name = name.Contains('.') ? name.Split('.')[^1] : name,
                });

            graph.AddEdge(new RepositoryGraphEdge { From = fromTypeId, To = toId, Relation = EdgeType.Inherits });
        }
    }

    private static string ResolveRelativeModule(string currentDirDotted, int dotCount, string moduleSuffix)
    {
        var segments = string.IsNullOrEmpty(currentDirDotted)
            ? []
            : currentDirDotted.Split('.').ToList();

        var levelsUp = dotCount - 1;
        for (var i = 0; i < levelsUp && segments.Count > 0; i++)
            segments.RemoveAt(segments.Count - 1);

        var basePath = string.Join('.', segments);
        if (string.IsNullOrEmpty(moduleSuffix)) return basePath;
        return string.IsNullOrEmpty(basePath) ? moduleSuffix : $"{basePath}.{moduleSuffix}";
    }

    private static string ModulePathFor(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var noExt = normalized.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^3]
            : normalized;

        var segments = noExt.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0 && string.Equals(segments[^1], "__init__", StringComparison.Ordinal))
            segments.RemoveAt(segments.Count - 1);

        return string.Join('.', segments);
    }

    private static string DirDottedPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var dir        = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? "";
        return dir.Length == 0 ? "" : string.Join('.', dir.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int IndentOf(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return i;
    }

    private static string StripLineComment(string line)
    {
        var idx = line.IndexOf('#');
        return idx >= 0 ? line[..idx] : line;
    }

    private static int NetParens(string text)
    {
        var net = 0;
        foreach (var c in text)
        {
            if (c == '(') net++;
            else if (c == ')') net--;
        }
        return net;
    }
}
