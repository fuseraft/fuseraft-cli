using fuseraft.Core;
using fuseraft.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace fuseraft.Infrastructure.Knowledge;

/// <summary>
/// Gap 9 — Knowledge Lifecycle Management.
///
/// <para>
/// Implements time-based retention policies for every knowledge subsystem:
/// <list type="bullet">
///   <item>Archives superseded ADRs to the decisions archive directory.</item>
///   <item>Demotes approved repository memories that have not been reinforced recently.</item>
///   <item>Decays old <c>Verified</c> provenance claims to <c>Inferred</c>.</item>
///   <item>Prunes orphaned repository graph nodes.</item>
///   <item>Compacts the provenance registry by archiving expired claims.</item>
/// </list>
/// </para>
///
/// <para>
/// All operations are <b>dry-run by default</b>. Pass <c>apply: true</c> to commit
/// changes to disk. The returned <see cref="GcReport"/> describes every action that
/// was taken (or would be taken in dry-run mode).
/// </para>
/// </summary>
public sealed class KnowledgeLifecycleManager
{
    private readonly AdrStore              _adrStore;
    private readonly RepositoryMemoryStore _memoryStore;
    private readonly RepositoryGraphStore  _graphStore;
    private readonly ProvenanceRegistry    _provenance;

    public KnowledgeLifecycleManager(
        AdrStore              adrStore,
        RepositoryMemoryStore memoryStore,
        RepositoryGraphStore  graphStore,
        ProvenanceRegistry    provenance)
    {
        _adrStore    = adrStore;
        _memoryStore = memoryStore;
        _graphStore  = graphStore;
        _provenance  = provenance;
    }

    /// <summary>
    /// Loads a <see cref="LifecyclePolicy"/> from <paramref name="path"/> (YAML).
    /// Returns defaults when the file is absent or cannot be parsed.
    /// </summary>
    public static LifecyclePolicy LoadPolicy(string? path = null)
    {
        var file = path ?? FuseraftPaths.LocalLifecycleConfig;
        if (!File.Exists(file)) return new LifecyclePolicy();
        try
        {
            var yaml = File.ReadAllText(file);
            var des  = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            return des.Deserialize<LifecyclePolicy>(yaml) ?? new LifecyclePolicy();
        }
        catch { return new LifecyclePolicy(); }
    }

    /// <summary>
    /// Runs all lifecycle policies and returns a <see cref="GcReport"/> describing
    /// what was or would be changed. When <paramref name="apply"/> is <c>false</c>,
    /// nothing is written to disk (dry-run).
    /// </summary>
    public async Task<GcReport> RunAsync(
        LifecyclePolicy   policy,
        bool              apply,
        CancellationToken ct = default)
    {
        var archivedDecisions  = await ArchiveSupersededAdrsAsync(policy, apply, ct);
        var demotedMemories    = await DemoteAgedMemoriesAsync(policy, apply, ct);
        var prunedMemories     = await PruneStaleMemoriesAsync(policy, apply, ct);
        var decayedClaims      = await DecayProvenanceAsync(policy, apply, ct);
        var prunedNodes        = await PruneOrphanedNodesAsync(policy, apply, ct);
        var archivedProvenance = await CompactProvenanceAsync(policy, apply, ct);

        return new GcReport
        {
            ArchivedDecisionIds   = archivedDecisions,
            DemotedMemoryIds      = demotedMemories,
            PrunedMemoryIds       = prunedMemories,
            DecayedClaimIds       = decayedClaims,
            PrunedNodeIds         = prunedNodes,
            ArchivedProvenanceIds = archivedProvenance,
        };
    }

    // ── Step 1 — Archive superseded ADRs ─────────────────────────────────────

    private async Task<IReadOnlyList<string>> ArchiveSupersededAdrsAsync(
        LifecyclePolicy policy, bool apply, CancellationToken ct)
    {
        var all        = await _adrStore.LoadAllAsync(ct);
        var cutoff     = policy.AdrRetentionDays > 0
            ? DateTimeOffset.UtcNow.AddDays(-policy.AdrRetentionDays)
            : DateTimeOffset.MaxValue; // 0 = archive any superseded ADR immediately

        var eligible = all
            .Where(e => e.Status.Equals("Superseded", StringComparison.OrdinalIgnoreCase))
            .Where(e =>
            {
                // When AdrRetentionDays = 0 all superseded ADRs are eligible.
                if (policy.AdrRetentionDays == 0) return true;
                // Otherwise, require the ADR's date to be older than the retention window.
                // AdrEntry.Date is a string; parse best-effort; include when unparseable.
                return !DateTimeOffset.TryParse(e.Date, out var d) || d < cutoff;
            })
            .ToList();

        if (!apply) return eligible.Select(e => e.Id).ToList();

        var archived = new List<string>();
        foreach (var entry in eligible)
        {
            if (await _adrStore.ArchiveAsync(entry.Id, ct))
                archived.Add(entry.Id);
        }
        return archived;
    }

    // ── Step 2 — Demote aged repository memories ─────────────────────────────

    private async Task<IReadOnlyList<string>> DemoteAgedMemoriesAsync(
        LifecyclePolicy policy, bool apply, CancellationToken ct)
    {
        if (policy.MemoryReinforceWindowDays <= 0) return [];

        var cutoff  = DateTimeOffset.UtcNow.AddDays(-policy.MemoryReinforceWindowDays);
        var entries = await _memoryStore.LoadApprovedAsync(ct);

        var eligible = entries
            .Where(e => e.LastReinforcedAt < cutoff)
            .ToList();

        if (!apply) return eligible.Select(e => e.Id).ToList();

        var demoted = new List<string>();
        foreach (var entry in eligible)
        {
            await _memoryStore.SaveAsync(entry with { Status = "Candidate" }, ct);
            demoted.Add(entry.Id);
        }
        return demoted;
    }

    // ── Step 3 — Prune stale Candidate memories ──────────────────────────────

    private async Task<IReadOnlyList<string>> PruneStaleMemoriesAsync(
        LifecyclePolicy policy, bool apply, CancellationToken ct)
    {
        if (policy.MemoryCandidatePruningDays <= 0) return [];

        var cutoff    = DateTimeOffset.UtcNow.AddDays(-policy.MemoryCandidatePruningDays);
        var candidates = await _memoryStore.LoadCandidatesAsync(ct);

        var eligible = candidates
            .Where(e => e.LastReinforcedAt < cutoff)
            .ToList();

        if (!apply) return eligible.Select(e => e.Id).ToList();

        var pruned = new List<string>();
        foreach (var entry in eligible)
        {
            await _memoryStore.DeleteAsync(entry.Id, ct);
            pruned.Add(entry.Id);
        }
        return pruned;
    }

    // ── Step 4 — Decay provenance confidence ─────────────────────────────────

    private async Task<IReadOnlyList<string>> DecayProvenanceAsync(
        LifecyclePolicy policy, bool apply, CancellationToken ct)
    {
        if (policy.ConfidenceDecayDays <= 0) return [];
        return await _provenance.DecayAsync(policy.ConfidenceDecayDays, apply, ct);
    }

    // ── Step 5 — Prune orphaned graph nodes ──────────────────────────────────

    private async Task<IReadOnlyList<string>> PruneOrphanedNodesAsync(
        LifecyclePolicy policy, bool apply, CancellationToken ct)
    {
        if (policy.OrphanedNodeGracePeriodDays <= 0) return [];

        var cutoff  = DateTimeOffset.UtcNow.AddDays(-policy.OrphanedNodeGracePeriodDays);
        var graph   = await _graphStore.LoadAsync(ct);

        // Build set of all node IDs that appear in at least one edge.
        var connected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            connected.Add(edge.From);
            connected.Add(edge.To);
        }

        // Orphaned: no edges (from or to), not an ADR node (has its own archive path),
        // and old enough to be past the grace period.
        var orphans = graph.Nodes
            .Where(n => n.Kind != NodeType.Adr
                     && n.Kind != NodeType.Violation
                     && !connected.Contains(n.Id)
                     && n.Timestamp < cutoff)
            .Select(n => n.Id)
            .ToList();

        if (!apply || orphans.Count == 0)
            return orphans;

        var orphanSet = new HashSet<string>(orphans, StringComparer.Ordinal);
        graph.Nodes.RemoveAll(n => orphanSet.Contains(n.Id));
        await _graphStore.SaveAsync(graph, ct);
        return orphans;
    }

    // ── Step 6 — Compact provenance registry ─────────────────────────────────

    private async Task<IReadOnlyList<string>> CompactProvenanceAsync(
        LifecyclePolicy policy, bool apply, CancellationToken ct)
    {
        bool ShouldArchive(ClaimRecord r)
        {
            // Always archive records whose ExpiresAt is in the past.
            if (r.ExpiresAt.HasValue && r.ExpiresAt.Value < DateTimeOffset.UtcNow)
                return true;

            // Additionally archive records older than MaxProvenanceAgeDays (when set).
            if (policy.MaxProvenanceAgeDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-policy.MaxProvenanceAgeDays);
                var age    = r.VerifiedAt ?? r.ObservedAt;
                if (age < cutoff) return true;
            }

            return false;
        }

        var archivePath = FuseraftPaths.ExpandProjectPaths(
            FuseraftPaths.LocalProvenanceArchive,
            FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory()));
        var archived = await _provenance.CompactAsync(ShouldArchive, archivePath, apply, ct);

        return archived.Select(r => r.Id).ToList();
    }
}
