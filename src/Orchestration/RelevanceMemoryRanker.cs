using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

/// <summary>
/// Ranks memory entries by relevance to the current task's intent signals,
/// replacing the previous alphabetical-by-type sort.
///
/// <para>Scoring:</para>
/// <list type="bullet">
///   <item>+2 per signal term found in the entry's Name or Description.</item>
///   <item>+1 per signal term found in the entry's Body.</item>
///   <item>Type priority added as a tiebreaker: feedback=4, project=3, user=2, reference=1.</item>
/// </list>
/// </summary>
public sealed class RelevanceMemoryRanker : IMemoryRanker
{
    private static readonly Dictionary<string, int> TypePriority =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["feedback"]  = 4,
            ["project"]   = 3,
            ["user"]      = 2,
            ["reference"] = 1,
        };

    public IReadOnlyList<MemoryEntry> Rank(
        IReadOnlyList<MemoryEntry> entries,
        IntentSignals              signals)
    {
        if (entries.Count == 0) return entries;

        var allTerms = signals.Keywords
            .Concat(signals.ReferencedSymbols)
            .Concat(signals.FailurePatterns)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return entries
            .Select(e => (Entry: e, Score: ComputeScore(e, allTerms)))
            .OrderByDescending(t => t.Score)
            .ThenBy(t => t.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Entry)
            .ToList();
    }

    private static int ComputeScore(MemoryEntry entry, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
            return TypePriority.GetValueOrDefault(entry.Type, 0);

        var header = $"{entry.Name} {entry.Description}";
        var body   = entry.Body;

        int score = 0;
        foreach (var term in terms)
        {
            if (header.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 2;
            else if (!string.IsNullOrWhiteSpace(body) &&
                     body.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 1;
        }

        score += TypePriority.GetValueOrDefault(entry.Type, 0);
        return score;
    }
}
