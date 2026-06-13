using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace fuseraft.Infrastructure.Chat;

/// <summary>
/// Tries a chain of <see cref="IChatClient"/> instances in order, falling over to the next
/// when a classifiable provider error occurs (rate limit, context exceeded, server error, etc.).
///
/// <para>
/// Each slot is tried in sequence. When a slot throws an exception whose
/// <see cref="FailoverReason"/> is in <paramref name="falloverOn"/>, the error is logged to
/// stderr and the next slot is attempted. If all slots fail, the last exception is re-thrown.
/// </para>
///
/// <para>
/// For streaming responses, fallover is only possible before the first chunk is yielded.
/// Once chunks are flowing the caller has already received partial output and switching
/// models mid-stream would produce incoherent results — mid-stream exceptions propagate as-is.
/// </para>
/// </summary>
internal sealed class FalloverChatClient(
    IChatClient[] chain,
    IReadOnlySet<FailoverReason> falloverOn,
    ILogger? logger = null) : IChatClient
{
    public object? GetService(Type serviceType, object? serviceKey = null)
        => chain[0].GetService(serviceType, serviceKey);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messages = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();
        Exception? lastEx = null;

        for (int i = 0; i < chain.Length; i++)
        {
            try
            {
                return await chain[i].GetResponseAsync(messages, options, cancellationToken);
            }
            catch (Exception ex) when (ShouldFallover(ex, i))
            {
                lastEx = ex;
                LogFallover(ex, i);
            }
        }

        throw lastEx!;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();
        Exception? lastEx = null;

        for (int i = 0; i < chain.Length; i++)
        {
            bool didFallover = false;
            var en = chain[i]
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            try
            {
                bool hasMore;
                try
                {
                    hasMore = await en.MoveNextAsync();
                }
                catch (Exception ex) when (ShouldFallover(ex, i))
                {
                    lastEx      = ex;
                    didFallover = true;
                    LogFallover(ex, i);
                    hasMore = false;
                }

                if (!didFallover)
                {
                    // First chunk is in hand — stream is live. Yield it and all remaining chunks.
                    // Mid-stream exceptions propagate as-is: the caller has already received
                    // partial output and we cannot coherently restart on a different model.
                    while (hasMore)
                    {
                        yield return en.Current;
                        hasMore = await en.MoveNextAsync();
                    }
                    yield break;
                }
            }
            finally
            {
                await en.DisposeAsync();
            }
        }

        throw lastEx ?? new InvalidOperationException(
            $"All {chain.Length} model(s) in the fallover chain exhausted without a successful response.");
    }

    public void Dispose()
    {
        foreach (var c in chain) c.Dispose();
    }

    private bool ShouldFallover(Exception ex, int slotIndex)
    {
        if (slotIndex >= chain.Length - 1) return false; // last slot — let it throw
        var reason = ProviderErrorClassifier.Classify(ex);
        return reason != FailoverReason.None && falloverOn.Contains(reason);
    }

    private void LogFallover(Exception ex, int fromSlot)
    {
        var reason   = ProviderErrorClassifier.Classify(ex);
        var nextSlot = fromSlot + 1;
        logger?.LogWarning(
            "[fallover] Slot {From}/{Total} failed ({Reason}: {Message}). Trying slot {Next}/{Total}.",
            fromSlot + 1, chain.Length, reason, Trim(ex.Message, 120), nextSlot + 1, chain.Length);
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
