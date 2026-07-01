using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Repository;

/// <summary>
/// Builds and incrementally maintains the <see cref="RepositoryGraph"/> by scanning source files.
///
/// <para>
/// Owns everything language-agnostic — file discovery, locking, and store I/O. The actual
/// per-file structural parsing (declarations, scoping rules, <c>SymbolId</c> conventions) is
/// delegated to one or more <see cref="IRepositoryGraphStrategy"/> instances, selected per file
/// via <see cref="IRepositoryGraphStrategy.CanHandle"/>. Defaults to
/// <see cref="DotNetRepositoryGraphStrategy"/> when no strategies are supplied. Adding support
/// for another language means adding a new strategy — this class should not need to change.
/// </para>
/// </summary>
public sealed class RepositoryGraphBuilder
{
    private readonly RepositoryGraphStore _store;
    private readonly string _projectRoot;
    private readonly IReadOnlyList<IRepositoryGraphStrategy> _strategies;
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    public RepositoryGraphBuilder(
        RepositoryGraphStore store,
        string? projectRoot = null,
        IEnumerable<IRepositoryGraphStrategy>? strategies = null)
    {
        _store       = store;
        _projectRoot = Path.GetFullPath(projectRoot ?? Directory.GetCurrentDirectory());
        _strategies  = strategies?.ToList() ?? [new DotNetRepositoryGraphStrategy()];
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds nodes for <paramref name="absoluteFilePath"/> in the persisted graph.
    /// Removes stale nodes first, then re-scans the file and saves.
    /// No-ops for files no registered strategy can handle.
    /// </summary>
    public async Task RebuildFileAsync(string absoluteFilePath, CancellationToken ct = default)
    {
        var strategy = _strategies.FirstOrDefault(s => s.CanHandle(absoluteFilePath));
        if (strategy is null) return;
        if (!File.Exists(absoluteFilePath)) return;

        await _buildLock.WaitAsync(ct);
        try
        {
            var graph    = await _store.LoadAsync(ct);
            var relative = RelativePath(absoluteFilePath);
            graph.RemoveFile(relative);
            strategy.ScanFile(absoluteFilePath, relative, graph);
            await _store.SaveAsync(graph, ct);
        }
        finally { _buildLock.Release(); }
    }

    /// <summary>
    /// Full initial build: scans all files matched by any registered strategy's
    /// <see cref="IRepositoryGraphStrategy.FileGlobs"/> under <paramref name="directory"/> (or the
    /// project root when omitted) and overwrites the persisted graph.
    /// Returns the number of nodes created.
    /// </summary>
    public async Task<(int Nodes, int Edges)> BuildAllAsync(
        string? directory = null,
        CancellationToken ct = default)
    {
        var root  = directory is not null ? Path.GetFullPath(directory) : _projectRoot;
        var graph = new RepositoryGraph();

        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(string Absolute, IRepositoryGraphStrategy Strategy)>();
        foreach (var strategy in _strategies)
        {
            foreach (var glob in strategy.FileGlobs)
            {
                foreach (var f in Directory.GetFiles(root, glob, SearchOption.AllDirectories))
                {
                    if (IsBuildArtifact(f)) continue;
                    if (!seen.Add(f)) continue;
                    files.Add((f, strategy));
                }
            }
        }

        foreach (var (f, strategy) in files)
        {
            if (ct.IsCancellationRequested) break;
            var relative = RelativePath(f, root);
            strategy.ScanFile(f, relative, graph);
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

    private static bool IsBuildArtifact(string path) =>
        path.Contains("/obj/", StringComparison.Ordinal) ||
        path.Contains("\\obj\\", StringComparison.Ordinal) ||
        path.Contains("/bin/", StringComparison.Ordinal) ||
        path.Contains("\\bin\\", StringComparison.Ordinal);
}
