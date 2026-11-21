using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Agents;

/// <summary>Keeps each conversation's requests append-only between trims, which cut a request past its budget to well below it.</summary>
// Re-trimming every request from the stored history moved the cut as the history grew, so near the budget almost every
// round rewrote an earlier message. Grok on Bedrock then served nothing from cache, not even the messages before the
// edit: 2.6M uncached tokens in 10 requests of one session. Here one trim costs one miss and frees a band of the budget.
internal sealed class CacheStableRequestView(
    Func<IList<ChatMessage>, int, CancellationToken, Task<IEnumerable<ChatMessage>>> trim,
    double lowWaterRatio = CacheStableRequestView.DefaultLowWaterRatio)
{
    /// <summary>Share of the budget a trim cuts down to. The rest is the band the request grows into before the next trim.</summary>
    internal const double DefaultLowWaterRatio = 0.6;

    // Subagent runs share one client, each with its own conversation; past this many, the oldest views are dropped.
    private const int MaxConversations = 16;

    private sealed record View(int[] SourceHashes, List<ChatMessage> Sent, int? CharsAfterTrim, long Stamp);

    private readonly ConcurrentDictionary<string, View> _views = new();
    private long _clock;

    /// <summary>The messages to send for <paramref name="stored"/>, trimmed only when they pass <paramref name="maxChars"/> (0: never).</summary>
    public async Task<IReadOnlyList<ChatMessage>> RenderAsync(
        string? run, IReadOnlyList<ChatMessage> stored, int maxChars, CancellationToken ct)
    {
        var key = run ?? string.Empty;
        var hashes = new int[stored.Count];
        for (var i = 0; i < stored.Count; i++) hashes[i] = RequestPrefixTracker.Hash(stored[i]);

        List<ChatMessage> sent;
        int? charsAfterTrim = null;
        if (_views.TryGetValue(key, out var view) && Extends(hashes, view.SourceHashes))
        {
            sent = [.. view.Sent];
            for (var i = view.SourceHashes.Length; i < stored.Count; i++) sent.Add(stored[i]);
            charsAfterTrim = view.CharsAfterTrim;
        }
        else
        {
            sent = [.. stored];
        }

        // A trim that couldn't get under the budget (one message bigger than it, say) waits until the request has grown
        // by another band, rather than missing the cache on every round to get nowhere.
        var chars = AgentContextCompactionFilters.EstimateTotalChars(sent);
        var lowWater = (int)(maxChars * lowWaterRatio);
        if (maxChars > 0 && chars > maxChars && (charsAfterTrim is not { } after || chars - after >= maxChars - lowWater))
        {
            sent = [.. await trim(stored as IList<ChatMessage> ?? [.. stored], lowWater, ct)];
            charsAfterTrim = AgentContextCompactionFilters.EstimateTotalChars(sent);
        }

        _views[key] = new View(hashes, sent, charsAfterTrim, Interlocked.Increment(ref _clock));
        if (_views.Count > MaxConversations)
            foreach (var stale in _views.OrderBy(kv => kv.Value.Stamp).Take(_views.Count - MaxConversations).ToList())
                _views.TryRemove(stale.Key, out _);
        return sent;
    }

    private static bool Extends(int[] current, int[] previous)
    {
        if (current.Length < previous.Length) return false;
        for (var i = 0; i < previous.Length; i++)
            if (current[i] != previous[i]) return false;
        return true;
    }
}
