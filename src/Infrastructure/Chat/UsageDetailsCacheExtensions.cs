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
        details?.AdditionalCounts?.TryGetValue("CacheCreationInputTokens", out var v) == true
            ? v : 0;
}
