using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Chat;

/// <summary>
/// Reads the normalized cache-token counts that <see cref="CacheUsageBackfillChatClient"/>
/// (OpenAI-compatible providers) and <see cref="AnthropicPromptCachingChatClient"/> (native
/// Anthropic) both write into <see cref="UsageDetails.AdditionalCounts"/>, so REPL/orchestrator
/// usage tracking reads one shape regardless of which provider produced the response.
/// </summary>
internal static class UsageDetailsCacheExtensions
{
    /// <summary>Prompt tokens served from cache — the realized discount. Reported by every
    /// provider fuseraft talks to that supports caching at all (Anthropic, and automatically by
    /// OpenAI-compatible providers like xAI, OpenAI, DeepSeek).</summary>
    public static long CacheReadTokens(this UsageDetails? details) =>
        details?.AdditionalCounts?.TryGetValue(CacheUsageBackfillChatClient.CacheReadInputTokensKey, out var v) == true
            ? v : 0;

    /// <summary>Prompt tokens newly written to cache this call — a one-time, higher-priced cost.
    /// Anthropic-only; OpenAI-compatible providers cache automatically with no separate write
    /// price to report.</summary>
    public static long CacheCreationTokens(this UsageDetails? details) =>
        details?.AdditionalCounts?.TryGetValue(CacheCreationInputTokensKey, out var v) == true
            ? v : 0;

    private const string CacheCreationInputTokensKey = "CacheCreationInputTokens";

    /// <summary>
    /// Prompt tokens this call paid full (or cache-write) price for — the input that was not
    /// served from cache. The two provider families disagree on what InputTokenCount means:
    /// OpenAI-compatible providers report the whole prompt, cached part included, while
    /// Anthropic.SDK maps Anthropic's <c>input_tokens</c>, which already excludes cache reads
    /// and cache writes. Anthropic.SDK always sets the cache-creation key (0 or not) and the
    /// OpenAI backfill never does, which tells the two apart.
    /// </summary>
    public static long UncachedInputTokens(this UsageDetails? details)
    {
        var input = details?.InputTokenCount ?? 0;
        if (details?.AdditionalCounts?.ContainsKey(CacheCreationInputTokensKey) == true)
            return input + details.CacheCreationTokens();
        return Math.Max(0, input - details.CacheReadTokens());
    }
}
