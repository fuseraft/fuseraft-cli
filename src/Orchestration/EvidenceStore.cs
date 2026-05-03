using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using fuseraft.Core.Models;
using Microsoft.Extensions.Logging;

namespace fuseraft.Orchestration;

/// <summary>
/// Manages the structured evidence graph: a typed, queryable log of every observable
/// action taken during a session.
///
/// <para>
/// <see cref="ChangeTracker"/> calls <see cref="RecordAsync"/> after each agent turn to
/// add typed nodes. Evidence contracts query the store directly via the
/// <see cref="QueryNodes"/> method, which is richer than scanning the flat
/// <c>changes.json</c> format.
/// </para>
///
/// <para>
/// The graph is persisted to <c>.fuseraft/evidence.json</c> (configurable) and loaded
/// lazily on first query so sessions that do not use evidence contracts incur no overhead.
/// </para>
/// </summary>
public sealed class EvidenceStore
{
    private readonly string _graphPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<EvidenceStore>? _logger;
    private string? _sessionId;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public EvidenceStore(string graphPath, ILogger<EvidenceStore>? logger = null)
    {
        _graphPath = graphPath;
        _logger    = logger;
    }

    /// <summary>
    /// Stamps the active session ID so queries can filter to the current session's nodes.
    /// Call once at session startup, after the checkpoint is established.
    /// </summary>
    public async Task SetSessionIdAsync(string sessionId, CancellationToken ct = default)
    {
        _sessionId = sessionId;

        await _lock.WaitAsync(ct);
        try
        {
            var graph = await LoadAsync(ct);
            graph = graph with { ActiveSessionId = sessionId };
            await SaveAsync(graph, ct);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Appends a batch of evidence nodes (produced from one agent turn) to the graph,
    /// and optionally adds edges between related nodes.
    /// </summary>
    public async Task RecordAsync(
        IReadOnlyList<EvidenceNode> nodes,
        IReadOnlyList<EvidenceEdge>? edges = null,
        CancellationToken ct = default)
    {
        if (nodes.Count == 0) return;

        await _lock.WaitAsync(ct);
        try
        {
            var graph = await LoadAsync(ct);

            var updatedNodes = new List<EvidenceNode>(graph.Nodes);
            updatedNodes.AddRange(nodes);

            var updatedEdges = new List<EvidenceEdge>(graph.Edges);
            if (edges is not null) updatedEdges.AddRange(edges);

            graph = graph with { Nodes = updatedNodes, Edges = updatedEdges };
            await SaveAsync(graph, ct);
        }
        finally { _lock.Release(); }
    }

    // Query API

    /// <summary>
    /// Returns all nodes that satisfy <paramref name="predicate"/>, filtered to the
    /// current session when a session ID has been stamped.
    /// </summary>
    public async Task<IReadOnlyList<EvidenceNode>> QueryNodes(
        Func<EvidenceNode, bool> predicate,
        CancellationToken ct = default)
    {
        var graph = await LoadAsync(ct);
        var sid = graph.ActiveSessionId;
        var source = sid is not null
            ? graph.Nodes.Where(n => string.Equals(n.SessionId, sid, StringComparison.Ordinal))
            : (IEnumerable<EvidenceNode>)graph.Nodes;

        return source.Where(predicate).ToList();
    }

    /// <summary>
    /// Returns all edges that connect nodes matching the given relation.
    /// </summary>
    public async Task<IReadOnlyList<EvidenceEdge>> QueryEdges(
        string relation,
        CancellationToken ct = default)
    {
        var graph = await LoadAsync(ct);
        return graph.Edges
            .Where(e => string.Equals(e.Relation, relation, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Returns the set of file paths written during the current session.
    /// </summary>
    public async Task<IReadOnlySet<string>> GetWrittenFilePathsAsync(CancellationToken ct = default)
    {
        var nodes = await QueryNodes(
            n => string.Equals(n.NodeType, "FileWrite", StringComparison.OrdinalIgnoreCase)
              && !string.IsNullOrWhiteSpace(n.Path),
            ct);

        return nodes
            .Select(n => n.Path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns all shell commands that succeeded (ExitCode == 0) during the current session.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSucceededCommandsAsync(CancellationToken ct = default)
    {
        var nodes = await QueryNodes(
            n => string.Equals(n.NodeType, "CommandRun", StringComparison.OrdinalIgnoreCase)
              && n.ExitCode == 0
              && !string.IsNullOrWhiteSpace(n.Command),
            ct);

        return nodes.Select(n => n.Command!).ToList();
    }

    /// <summary>
    /// Returns all TestResult nodes from the current session.
    /// </summary>
    public async Task<IReadOnlyList<EvidenceNode>> GetTestResultsAsync(CancellationToken ct = default) =>
        await QueryNodes(
            n => string.Equals(n.NodeType, "TestResult", StringComparison.OrdinalIgnoreCase),
            ct);

    /// <summary>
    /// Returns all symbol-dependency nodes related to <paramref name="filePath"/>:
    /// every <c>SymbolDefinition</c> node whose <c>Path</c> matches the file, plus
    /// every <c>SymbolReference</c> node where the reference site (<c>Path</c>) or the
    /// definition site (<c>TargetFile</c>) matches the file.
    /// Not session-scoped — symbol nodes written by the Archaeologist persist across
    /// sessions and remain valid for the lifetime of the discovery brief.
    /// </summary>
    public async Task<IReadOnlyList<EvidenceNode>> QuerySymbolDependenciesAsync(
        string filePath,
        CancellationToken ct = default)
    {
        var graph = await LoadAsync(ct);
        return graph.Nodes
            .Where(n =>
                (string.Equals(n.NodeType, "SymbolDefinition", StringComparison.OrdinalIgnoreCase)
                    && PathsMatch(n.Path, filePath)) ||
                (string.Equals(n.NodeType, "SymbolReference", StringComparison.OrdinalIgnoreCase)
                    && (PathsMatch(n.Path, filePath) || PathsMatch(n.TargetFile, filePath))))
            .ToList();
    }

    /// <summary>
    /// Returns the distinct file paths where a <c>SymbolDefinition</c> node for
    /// <paramref name="symbolName"/> was recorded. Used by <c>ChangeTracker</c> to populate
    /// <c>TargetFile</c> on <c>SymbolReference</c> nodes without a separate lookup argument.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindDefinitionFilesAsync(
        string symbolName,
        CancellationToken ct = default)
    {
        var graph = await LoadAsync(ct);
        return graph.Nodes
            .Where(n =>
                string.Equals(n.NodeType, "SymbolDefinition", StringComparison.OrdinalIgnoreCase)
                && string.Equals(n.SymbolName, symbolName, StringComparison.OrdinalIgnoreCase)
                && n.Path is not null)
            .Select(n => n.Path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Simple path equivalence: matches on equal, suffix, or prefix normalization.
    private static bool PathsMatch(string? a, string? b)
    {
        if (a is null || b is null) return false;
        a = a.Replace('\\', '/').TrimStart('.', '/');
        b = b.Replace('\\', '/').TrimStart('.', '/');
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase)
            || b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase);
    }

    // Helpers

    internal static string? HashContent(string? content)
    {
        if (content is null) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes)[..16]; // first 16 hex chars is enough
    }

    private async Task<EvidenceGraph> LoadAsync(CancellationToken ct)
    {
        if (!System.IO.File.Exists(_graphPath)) return new EvidenceGraph();

        try
        {
            var raw = await System.IO.File.ReadAllTextAsync(_graphPath, ct);
            return JsonSerializer.Deserialize<EvidenceGraph>(raw, JsonOpts) ?? new EvidenceGraph();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "EvidenceStore: failed to load '{Path}' — evidence graph reset.", _graphPath);
            return new EvidenceGraph();
        }
    }

    private async Task SaveAsync(EvidenceGraph graph, CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_graphPath));
        if (dir is not null) System.IO.Directory.CreateDirectory(dir);
        await System.IO.File.WriteAllTextAsync(_graphPath, JsonSerializer.Serialize(graph, JsonOpts), ct);
    }
}
