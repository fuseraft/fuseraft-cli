namespace fuseraft.Orchestration.Context;

/// <summary>
/// Ranks <see cref="RetrievedItem"/> results by confidence tier and trims them to a
/// character budget. Expired items are excluded entirely.
///
/// <para>Tier priority (ascending rank number = higher priority):</para>
/// <list type="bullet">
///   <item><c>Verified</c> — two or more hard evidence sources (rank 0)</item>
///   <item><c>Inferred</c> — one hard source or ADR / RepositoryMemory (rank 1)</item>
///   <item><c>Assumed</c> — AgentAssertion only (rank 2)</item>
///   <item><c>Guessed</c> — no provenance (rank 3)</item>
/// </list>
/// </summary>
public static class ContextBudgeter
{
    private static readonly Dictionary<string, int> TierRank =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Verified"] = 0,
            ["Inferred"] = 1,
            ["Assumed"]  = 2,
            ["Guessed"]  = 3,
        };

    /// <summary>
    /// Filters expired items, sorts by confidence tier, and returns only as many items
    /// as fit within <paramref name="maxChars"/> (estimated by title + summary length).
    /// </summary>
    public static IReadOnlyList<RetrievedItem> Budget(
        IEnumerable<RetrievedItem> items,
        int maxChars)
    {
        var ranked = items
            .Where(i => !i.IsExpired)
            .OrderBy(i => TierRank.GetValueOrDefault(i.ConfidenceTier, 3))
            .ToList();

        if (maxChars <= 0)
            return ranked;

        var result    = new List<RetrievedItem>(ranked.Count);
        int remaining = maxChars;

        foreach (var item in ranked)
        {
            var cost = EstimateChars(item);
            if (cost > remaining) break;
            result.Add(item);
            remaining -= cost;
        }

        return result;
    }

    private static int EstimateChars(RetrievedItem item) =>
        (item.Result.Title?.Length ?? 0) +
        (item.Result.Summary?.Length ?? 0) +
        (item.Result.FilePath?.Length ?? 0) +
        60; // formatting overhead per entry
}
