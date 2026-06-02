using fuseraft.Core;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// Concrete knowledge layer backed by the ADR Registry (Gap 1) and Repository Semantic Graph (Gap 2).
///
/// <para>
/// A single instance is created in <c>OrchestratorBuilder</c> and shared across all orchestrators,
/// context assemblers, and plugin instances within a session so every subsystem reads and writes
/// the same in-memory state.
/// </para>
///
/// <para>
/// Later gaps extend this class: Gap 3 adds <see cref="RecordClaimAsync"/> via
/// <c>ProvenanceRegistry</c>; Gap 7 adds <see cref="RecordObjectiveAsync"/> via
/// <c>ObjectiveStore</c>.
/// </para>
/// </summary>
public sealed class KnowledgeLayer : IKnowledgeLayer
{
    private readonly AdrRegistry            _adrRegistry;
    private readonly RepositoryGraphStore   _graphStore;
    private readonly RepositoryGraphBuilder _graphBuilder;

    public KnowledgeLayer(
        AdrRegistry            adrRegistry,
        RepositoryGraphStore   graphStore,
        RepositoryGraphBuilder graphBuilder)
    {
        _adrRegistry  = adrRegistry;
        _graphStore   = graphStore;
        _graphBuilder = graphBuilder;
    }

    // ── Exposed subsystem accessors (for callers that need direct subsystem access) ──

    /// <summary>Direct access to the ADR registry for operations not expressible through <see cref="IKnowledgeLayer"/>.</summary>
    public AdrRegistry AdrRegistry => _adrRegistry;

    /// <summary>Direct access to the repository graph store for traversal operations.</summary>
    public RepositoryGraphStore GraphStore => _graphStore;

    /// <summary>Direct access to the graph builder for incremental rebuilds (e.g. from ChangeTracker).</summary>
    public RepositoryGraphBuilder GraphBuilder => _graphBuilder;

    // ── IKnowledgeLayer ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IEnumerable<KnowledgeResult>> SearchAsync(
        string query,
        IReadOnlyList<KnowledgeKind>? kinds = null,
        CancellationToken ct = default)
    {
        var results = new List<KnowledgeResult>();
        bool includeDecisions  = kinds is null || kinds.Contains(KnowledgeKind.Decision);
        bool includeGraphNodes = kinds is null || kinds.Contains(KnowledgeKind.GraphNode);

        if (includeDecisions)
        {
            var adrs = await _adrRegistry.SearchAsync(query: query, ct: ct);
            results.AddRange(adrs.Select(e => new KnowledgeResult
            {
                Id      = $"adr:{e.Id}",
                Kind    = KnowledgeKind.Decision,
                Title   = e.Title,
                Summary = e.Decision.Length > 200 ? e.Decision[..200] + "…" : e.Decision,
                Status  = e.Status,
                Tags    = e.Tags,
            }));
        }

        if (includeGraphNodes && !string.IsNullOrWhiteSpace(query))
        {
            var graph = await _graphStore.LoadAsync(ct);
            var q = query.Trim();
            var nodes = graph.Nodes
                .Where(n =>
                    (n.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    n.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(20);

            results.AddRange(nodes.Select(n => new KnowledgeResult
            {
                Id       = n.Id,
                Kind     = KnowledgeKind.GraphNode,
                Title    = n.Name ?? n.Id,
                FilePath = n.FilePath,
                Status   = n.Kind.ToString(),
            }));
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task<KnowledgeArtifact?> RetrieveAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        // ADR IDs: "adr:ADR-0042" or bare "ADR-0042"
        var adrId = id.StartsWith("adr:", StringComparison.OrdinalIgnoreCase) ? id[4..] : id;
        if (adrId.StartsWith("ADR-", StringComparison.OrdinalIgnoreCase))
        {
            var entry = await _adrRegistry.GetByIdAsync(adrId, ct);
            if (entry is not null)
                return new KnowledgeArtifact { Id = id, Kind = KnowledgeKind.Decision, Decision = entry };
        }

        // Graph node IDs: "type:Ns.Class", "method:Ns.Class.Method", "file:rel/path.cs", etc.
        var graph = await _graphStore.LoadAsync(ct);
        var node  = graph.FindById(id);
        if (node is not null)
            return new KnowledgeArtifact { Id = id, Kind = KnowledgeKind.GraphNode, GraphNode = node };

        return null;
    }

    /// <inheritdoc/>
    public async Task<AdrEntry> RecordDecisionAsync(AdrEntry entry, CancellationToken ct = default)
    {
        await _adrRegistry.SaveAsync(entry, ct);
        if (entry.Governs.Count > 0)
            await _graphBuilder.UpsertAdrNodeAsync(entry, ct);
        return entry;
    }

    /// <inheritdoc/>
    /// <remarks>Not yet implemented — Gap 3 (Provenance and Confidence Tracking).</remarks>
    public Task<ClaimRecord> RecordClaimAsync(
        string claim,
        IReadOnlyList<string> support,
        CancellationToken ct = default)
        => throw new NotImplementedException("RecordClaimAsync is implemented in Gap 3.");

    /// <inheritdoc/>
    /// <remarks>Not yet implemented — Gap 7 (Long-Horizon Objective Tracking).</remarks>
    public Task<Objective> RecordObjectiveAsync(Objective objective, CancellationToken ct = default)
        => throw new NotImplementedException("RecordObjectiveAsync is implemented in Gap 7.");
}
