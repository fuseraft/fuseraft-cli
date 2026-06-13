using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Chat;

/// <summary>
/// Wraps multiple <see cref="IChatClient"/> instances (one per API key) and rotates
/// through them when a slot returns 429 Too Many Requests.
///
/// <para>
/// After <see cref="TransientRetryHandler"/> exhausts its per-request retries and a 429 is
/// still returned, <see cref="KeyPoolChatClient"/> marks that slot with a 60-second cooldown
/// and immediately retries on the next available slot.  Slots clear automatically after the
/// cooldown expires.  If every slot is simultaneously cooled, the last 429 exception is
/// re-thrown rather than busy-waiting.
/// </para>
/// </summary>
internal sealed class KeyPoolChatClient(IChatClient[] slots) : IChatClient
{
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(60);

    // Per-slot cooldown expiry — written only under the 429 path (rare), read on every call.
    private readonly DateTimeOffset[] _cooldownUntil = new DateTimeOffset[slots.Length];

    // Tracks the last successful slot so subsequent calls start close to it, reducing
    // unnecessary rotation when the pool is healthy.
    private int _currentSlot = 0;

    public object? GetService(Type serviceType, object? serviceKey = null)
        => slots[0].GetService(serviceType, serviceKey);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messages = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();
        var start = Volatile.Read(ref _currentSlot);
        Exception? lastRateLimit = null;

        for (int tried = 0; tried < slots.Length; tried++)
        {
            var idx = (start + tried) % slots.Length;
            if (DateTimeOffset.UtcNow < _cooldownUntil[idx]) continue;

            try
            {
                var result = await slots[idx].GetResponseAsync(messages, options, cancellationToken);
                Interlocked.Exchange(ref _currentSlot, idx);
                return result;
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                _cooldownUntil[idx] = DateTimeOffset.UtcNow + CooldownDuration;
                lastRateLimit = ex;
                Console.Error.WriteLine(
                    $"[key-pool] Slot {idx + 1}/{slots.Length} rate-limited (429). " +
                    $"Cooling down for {CooldownDuration.TotalSeconds:0}s, rotating to next key.");
            }
        }

        throw lastRateLimit ?? new InvalidOperationException(
            $"All {slots.Length} API key(s) in the pool are rate-limited (429). " +
            "Add more keys via 'ApiKeys' or try again later.");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();
        var start = Volatile.Read(ref _currentSlot);
        Exception? lastRateLimit = null;

        for (int tried = 0; tried < slots.Length; tried++)
        {
            var idx = (start + tried) % slots.Length;
            if (DateTimeOffset.UtcNow < _cooldownUntil[idx]) continue;

            bool slot429 = false;
            var en = slots[idx]
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            try
            {
                bool hasMore;
                try
                {
                    hasMore = await en.MoveNextAsync();
                }
                catch (ClientResultException ex) when (ex.Status == 429)
                {
                    slot429 = true;
                    lastRateLimit = ex;
                    _cooldownUntil[idx] = DateTimeOffset.UtcNow + CooldownDuration;
                    Console.Error.WriteLine(
                        $"[key-pool] Slot {idx + 1}/{slots.Length} rate-limited (429, streaming). " +
                        $"Cooling down for {CooldownDuration.TotalSeconds:0}s, rotating to next key.");
                    hasMore = false;
                }

                if (!slot429)
                {
                    Interlocked.Exchange(ref _currentSlot, idx);
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

        throw lastRateLimit ?? new InvalidOperationException(
            $"All {slots.Length} API key(s) in the pool are rate-limited (429). " +
            "Add more keys via 'ApiKeys' or try again later.");
    }

    public void Dispose()
    {
        foreach (var slot in slots) slot.Dispose();
    }
}
