using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Knowledge;

/// <summary>
/// Enriches a <see cref="ContextSnapshot"/> with knowledge-layer state derived from
/// the ADR registry, objective manager, architecture scanner, repository memory store,
/// and provenance registry.
///
/// <para>
/// Called by <see cref="fuseraft.Orchestration.ConversationCompactor"/> after
/// <c>IContextSnapshotter.SnapshotAsync</c> so that compaction summaries include
/// active ADRs, objective progress, architecture violations, approved repository
/// memories, and expired provenance warnings — all without modifying the
/// selection-strategy snapshot path.
/// </para>
///
/// <para>All data sources are optional; missing ones are silently skipped.</para>
/// </summary>
public sealed class KnowledgeSnapshotEnricher
{
    private readonly AdrRegistry?              _adrRegistry;
    private readonly ObjectiveManager?         _objectiveManager;
    private readonly RepositoryMemoryStore?    _memoryStore;
    private readonly ProvenanceRegistry?       _provenance;
    private readonly string?                   _manifestPath;
    private readonly string?                   _projectRoot;

    private const int MaxActiveAdrs       = 10;
    private const int MaxTopMemories      = 5;
    private const int MaxExpiredWarnings  = 10;
    private const int MaxViolations       = 10;

    public KnowledgeSnapshotEnricher(
        AdrRegistry?           adrRegistry       = null,
        ObjectiveManager?      objectiveManager  = null,
        RepositoryMemoryStore? memoryStore       = null,
        ProvenanceRegistry?    provenance        = null,
        string?                manifestPath      = null,
        string?                projectRoot       = null)
    {
        _adrRegistry      = adrRegistry;
        _objectiveManager = objectiveManager;
        _memoryStore      = memoryStore;
        _provenance       = provenance;
        _manifestPath     = manifestPath;
        _projectRoot      = projectRoot;
    }

    /// <summary>
    /// Returns a copy of <paramref name="snapshot"/> with the five knowledge-layer fields
    /// populated from the configured subsystems. All enrichment is best-effort:
    /// individual failures leave the corresponding field empty rather than throwing.
    /// </summary>
    public async Task<ContextSnapshot> EnrichAsync(
        ContextSnapshot   snapshot,
        CancellationToken ct = default)
    {
        var activeAdrs      = await LoadActiveAdrsAsync(ct);
        var objectiveState  = await LoadObjectiveStateAsync(ct);
        var archViolations  = await LoadArchViolationsAsync(ct);
        var topMemories     = await LoadTopMemoriesAsync(ct);
        var expiredWarnings = await LoadExpiredWarningsAsync(ct);

        return snapshot with
        {
            ActiveAdrs                = activeAdrs,
            ObjectiveState            = objectiveState,
            ArchitectureViolations    = archViolations,
            TopRepositoryMemories     = topMemories,
            ExpiredProvenanceWarnings = expiredWarnings,
        };
    }

    // ── Active ADRs ───────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<AdrSummary>> LoadActiveAdrsAsync(CancellationToken ct)
    {
        if (_adrRegistry is null) return [];
        try
        {
            var adrs = await _adrRegistry.GetActiveAsync(ct);
            return adrs
                .Take(MaxActiveAdrs)
                .Select(e => new AdrSummary(e.Id, e.Title, e.Status))
                .ToList();
        }
        catch { return []; }
    }

    // ── Objective state ───────────────────────────────────────────────────────

    private async Task<string?> LoadObjectiveStateAsync(CancellationToken ct)
    {
        if (_objectiveManager is null) return null;
        try { return await _objectiveManager.BuildActiveSummaryAsync(ct); }
        catch { return null; }
    }

    // ── Architecture violations ───────────────────────────────────────────────

    private async Task<IReadOnlyList<string>> LoadArchViolationsAsync(CancellationToken ct)
    {
        if (_manifestPath is null || _projectRoot is null) return [];
        try
        {
            var manifest = ArchitectureScanner.TryLoadManifest(_manifestPath);
            if (manifest is null) return [];

            var violations = await ArchitectureScanner.ScanAsync(manifest, _projectRoot, ct);
            return violations
                .Take(MaxViolations)
                .Select(v => $"{v.SourceLayer} → {v.TargetLayer}: {v.File} line {v.Line}")
                .ToList();
        }
        catch { return []; }
    }

    // ── Top approved repository memories ─────────────────────────────────────

    private async Task<IReadOnlyList<string>> LoadTopMemoriesAsync(CancellationToken ct)
    {
        if (_memoryStore is null) return [];
        try
        {
            var approved = await _memoryStore.LoadApprovedAsync(ct);
            return approved
                .OrderByDescending(m => m.ReinforcementCount)
                .Take(MaxTopMemories)
                .Select(m => m.Pattern.Length > 120 ? m.Pattern[..120] + "…" : m.Pattern)
                .ToList();
        }
        catch { return []; }
    }

    // ── Expired provenance warnings ───────────────────────────────────────────

    private async Task<IReadOnlyList<string>> LoadExpiredWarningsAsync(CancellationToken ct)
    {
        if (_provenance is null) return [];
        try
        {
            var now    = DateTimeOffset.UtcNow;
            var all    = await _provenance.GetAllAsync(ct);
            return all
                .Where(r => r.ExpiresAt.HasValue && r.ExpiresAt.Value < now)
                .OrderBy(r => r.ExpiresAt!.Value)
                .Take(MaxExpiredWarnings)
                .Select(r =>
                {
                    var claim = r.Claim.Length > 80 ? r.Claim[..80] + "…" : r.Claim;
                    return $"'{claim}' expired {r.ExpiresAt!.Value:yyyy-MM-dd HH:mm} UTC";
                })
                .ToList();
        }
        catch { return []; }
    }
}
