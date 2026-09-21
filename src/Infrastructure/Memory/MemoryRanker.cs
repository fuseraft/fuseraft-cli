using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Memory;

/// <summary>
/// Orders memory entries by overlap with the current task's terms, so the entries that
/// survive the prompt-block budget are the relevant ones.
/// </summary>
internal static class MemoryRanker
{
    // A bonus, not a strict tiebreaker: general feedback should not be evicted by a
    // reference entry that only matches one incidental term in its body.
    private static readonly Dictionary<string, int> TypePriority =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["feedback"]  = 4,
            ["project"]   = 3,
            ["user"]      = 2,
            ["reference"] = 1,
        };

    /// <summary>+2 per term in the name or description, else +1 per term in the body, plus type priority.</summary>
    internal static List<MemoryEntry> Rank(IEnumerable<MemoryEntry> entries, IReadOnlyList<string>? terms)
    {
        var distinct = (terms ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return entries
            .Select(e => (Entry: e, Score: Score(e, distinct)))
            .OrderByDescending(t => t.Score)
            .ThenBy(t => t.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Entry)
            .ToList();
    }

    private static int Score(MemoryEntry entry, IReadOnlyList<string> terms)
    {
        var score = TypePriority.GetValueOrDefault(entry.Type, 0);
        if (terms.Count == 0) return score;

        var header = $"{entry.Name} {entry.Description}";
        foreach (var term in terms)
        {
            if (header.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 2;
            else if (entry.Body.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 1;
        }
        return score;
    }
}
