using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Orchestration;

/// <summary>
/// A knowledge result enriched with provenance metadata for ranking by <see cref="ContextBudgeter"/>.
/// </summary>
public sealed record RetrievedItem
{
    public required KnowledgeResult Result { get; init; }

    /// <summary>Most recent claim for this artifact, or <c>null</c> when no provenance exists.</summary>
    public ClaimRecord? Provenance { get; init; }

    /// <summary>
    /// <c>true</c> when <see cref="Provenance"/> exists and its <c>ExpiresAt</c> is in the past.
    /// Expired items are excluded from broker output by <see cref="ContextBudgeter"/>.
    /// </summary>
    public bool IsExpired { get; init; }

    /// <summary>Effective confidence tier from provenance status, or <c>"Guessed"</c> when absent.</summary>
    public string ConfidenceTier => Provenance?.Status ?? "Guessed";
}

/// <summary>
/// Queries <see cref="IKnowledgeLayer"/> and the repository memory store using
/// <see cref="IntentSignals"/> and returns deduplicated, provenance-enriched results.
/// </summary>
public sealed class KnowledgeRetriever
{
    private readonly IKnowledgeLayer        _layer;
    private readonly RepositoryMemoryStore? _memoryStore;
    private readonly ProvenanceRegistry?    _provenance;

    public KnowledgeRetriever(
        IKnowledgeLayer        layer,
        RepositoryMemoryStore? memoryStore  = null,
        ProvenanceRegistry?    provenance   = null)
    {
        _layer       = layer;
        _memoryStore = memoryStore;
        _provenance  = provenance;
    }

    /// <summary>
    /// Queries the knowledge layer for each signal in <paramref name="signals"/>,
    /// deduplicates by ID, and enriches each result with its provenance record.
    /// </summary>
    public async Task<IReadOnlyList<RetrievedItem>> RetrieveAsync(
        IntentSignals signals,
        CancellationToken ct = default)
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<RetrievedItem>();

        // Symbols are more precise than keywords; try them first so deduplication
        // keeps the higher-quality match when both queries hit the same artifact.
        var queries = signals.ReferencedSymbols
            .Concat(signals.Keywords)
            .Concat(signals.FailurePatterns)
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        foreach (var q in queries)
        {
            IEnumerable<KnowledgeResult> batch;
            try   { batch = await _layer.SearchAsync(q, ct: ct); }
            catch { continue; }

            foreach (var r in batch)
            {
                if (!seen.Add(r.Id)) continue;

                ClaimRecord? provenance = null;
                bool         expired   = false;

                if (_provenance is not null)
                {
                    try
                    {
                        provenance = await _provenance.GetByArtifactAsync(r.Id, ct);
                        if (provenance?.ExpiresAt.HasValue == true &&
                            provenance.ExpiresAt!.Value < DateTimeOffset.UtcNow)
                            expired = true;
                    }
                    catch { /* best-effort */ }
                }

                results.Add(new RetrievedItem
                {
                    Result     = r,
                    Provenance = provenance,
                    IsExpired  = expired,
                });
            }
        }

        // Repository memory: approved patterns relevant to any query term.
        if (_memoryStore is not null && queries.Count > 0)
        {
            try
            {
                var memories = await _memoryStore.LoadApprovedAsync(ct);
                foreach (var mem in memories)
                {
                    var memId = $"repository-memory:{mem.Id}";
                    if (!seen.Add(memId)) continue;

                    bool relevant = queries.Any(q =>
                        mem.Pattern.Contains(q, StringComparison.OrdinalIgnoreCase));
                    if (!relevant) continue;

                    results.Add(new RetrievedItem
                    {
                        Result = new KnowledgeResult
                        {
                            Id      = memId,
                            Kind    = KnowledgeKind.Memory,
                            Title   = mem.Pattern.Length > 80 ? mem.Pattern[..80] + "…" : mem.Pattern,
                            Summary = $"Reinforced {mem.ReinforcementCount}× — confidence: {mem.Confidence}",
                            Status  = mem.Status,
                        },
                        Provenance = null,
                        IsExpired  = false,
                    });
                }
            }
            catch { /* best-effort */ }
        }

        return results;
    }
}
