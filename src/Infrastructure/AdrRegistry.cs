using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// Index and query layer over <see cref="AdrStore"/>.
///
/// Provides keyword search, status/tag filtering, supersession chain traversal,
/// and ID allocation. All reads go through the store; the registry adds no
/// in-memory cache — correctness over speed for a human-scale ADR corpus.
/// </summary>
public sealed class AdrRegistry
{
    private readonly AdrStore _store;

    public AdrRegistry(AdrStore store) => _store = store;

    // Search

    /// <summary>
    /// Returns ADRs matching all supplied filters. Passing empty/null values skips that filter.
    /// Query is checked against ID, title, context, decision text, and tags.
    /// </summary>
    public async Task<List<AdrEntry>> SearchAsync(
        string? query   = null,
        string? status  = null,
        string? tag     = null,
        CancellationToken ct = default)
    {
        var all = await _store.LoadAllAsync(ct);
        return all.Where(e => Matches(e, query, status, tag)).ToList();
    }

    // Lookup

    public Task<AdrEntry?> GetByIdAsync(string id, CancellationToken ct = default) =>
        _store.LoadAsync(id, ct);

    public async Task<List<AdrEntry>> GetActiveAsync(CancellationToken ct = default)
    {
        var all = await _store.LoadAllAsync(ct);
        return all.Where(e => e.Status.Equals("Accepted", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Walks the <c>Supersedes</c> chain starting from <paramref name="id"/>, returning
    /// entries in order from newest to oldest. Stops at the first entry with no
    /// <c>Supersedes</c> or at a cycle.
    /// </summary>
    public async Task<List<AdrEntry>> GetSupersessionChainAsync(string id, CancellationToken ct = default)
    {
        var chain   = new List<AdrEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = await _store.LoadAsync(id, ct);

        while (current is not null && visited.Add(current.Id))
        {
            chain.Add(current);
            if (current.Supersedes.Count == 0) break;
            current = await _store.LoadAsync(current.Supersedes[0], ct);
        }

        return chain;
    }

    // Write

    public async Task<AdrEntry> SaveAsync(AdrEntry entry, CancellationToken ct = default)
    {
        await _store.SaveAsync(entry, ct);
        return entry;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
        await _store.DeleteAsync(id, ct);

    // ID allocation

    public string NextId() => _store.NextId();

    // Helpers

    private static bool Matches(AdrEntry e, string? query, string? status, string? tag)
    {
        if (status is not null && !e.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
            return false;

        if (tag is not null && !e.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            var hit = e.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || e.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || e.Context.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || e.Decision.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || e.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (!hit) return false;
        }

        return true;
    }
}
