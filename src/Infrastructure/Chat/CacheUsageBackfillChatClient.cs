using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Chat;

/// <summary>
/// Backfills prompt-cache read counts onto responses from OpenAI-compatible providers
/// (OpenAI, xAI, DeepSeek, Google, Mistral, Azure OpenAI, …).
///
/// <para>
/// These providers already do automatic prompt caching and report it on the wire as
/// <c>usage.prompt_tokens_details.cached_tokens</c> — confirmed live against xAI, which returned
/// <c>cached_tokens: 192</c> out of 199 prompt tokens with zero special configuration. The OpenAI
/// .NET SDK deserializes that correctly onto
/// <c>OpenAI.Chat.ChatTokenUsage.InputTokenDetails.CachedTokenCount</c> — but
/// <c>Microsoft.Extensions.AI</c>'s <see cref="IChatClient"/> adapter drops that field when it
/// builds the abstract <see cref="UsageDetails"/> (confirmed the same way: only
/// AudioTokenCount/AcceptedPredictionTokenCount/RejectedPredictionTokenCount survive into
/// <see cref="UsageDetails.AdditionalCounts"/>). So fuseraft's usage tracking — REPL turn display,
/// <c>/context</c> session summary — never saw a cache discount that was already happening.
/// </para>
///
/// <para>
/// This wrapper reads the count back off <see cref="AIContent.RawRepresentation"/> /
/// <see cref="ChatResponse.RawRepresentation"/> (the original <c>OpenAI.Chat.ChatCompletion</c> /
/// <c>ChatTokenUsage</c> object survives untouched on both the non-streaming and streaming paths)
/// and copies it into <see cref="UsageDetails.AdditionalCounts"/> under the same
/// <see cref="CacheReadInputTokensKey"/> key <see cref="AnthropicPromptCachingChatClient"/> uses,
/// so downstream code reads one consistent key regardless of provider.
/// </para>
/// </summary>
internal sealed class CacheUsageBackfillChatClient(IChatClient inner) : IChatClient
{
    /// <summary>
    /// <see cref="UsageDetails.AdditionalCounts"/> key for prompt tokens served from cache.
    /// Shared with <see cref="AnthropicPromptCachingChatClient"/> so REPL/orchestrator usage
    /// tracking reads one key regardless of which provider produced the response.
    /// </summary>
    internal const string CacheReadInputTokensKey = "CacheReadInputTokens";

    public object? GetService(Type serviceType, object? serviceKey = null)
        => inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await inner.GetResponseAsync(messages, options, cancellationToken);
        if (response.Usage is { } usage)
            BackfillCacheReadTokens(usage, response.RawRepresentation);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (var usage in update.Contents.OfType<UsageContent>())
                BackfillCacheReadTokens(usage.Details, usage.RawRepresentation);
            yield return update;
        }
    }

    private static void BackfillCacheReadTokens(UsageDetails usage, object? rawRepresentation)
    {
        long? cached = rawRepresentation switch
        {
            OpenAI.Chat.ChatCompletion cc => cc.Usage?.InputTokenDetails?.CachedTokenCount,
            OpenAI.Chat.ChatTokenUsage tu => tu.InputTokenDetails?.CachedTokenCount,
            _ => null,
        };

        if (cached is > 0)
            (usage.AdditionalCounts ??= new())[CacheReadInputTokensKey] = cached.Value;
    }
}
