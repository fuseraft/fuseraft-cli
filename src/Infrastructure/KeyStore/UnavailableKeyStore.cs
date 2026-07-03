namespace fuseraft.Infrastructure.KeyStore;

/// <summary>
/// Thrown by <see cref="UnavailableKeyStore.StoreAsync"/> when a caller attempts to persist
/// an API key but no OS keychain is available. fuseraft never falls back to writing secrets
/// to disk in plaintext — callers should catch this, keep the key in memory for the current
/// process only, and point the user at a provider environment variable for future sessions.
/// </summary>
public sealed class KeyStoreUnavailableException(string message) : Exception(message);

// Returned when no native OS keychain is reachable (e.g. Linux without a running secret
// service, or any platform where the native store threw). fuseraft does not store API keys
// in plaintext on disk under any circumstances, so this store refuses to persist anything.
internal sealed class UnavailableKeyStore : IApiKeyStore
{
    public string StoreName => "no OS keychain available";

    public bool IsAvailable => false;

    public Task<string?> RetrieveAsync() => Task.FromResult<string?>(null);

    public Task StoreAsync(string apiKey) =>
        throw new KeyStoreUnavailableException(
            "No OS keychain is available on this system, and fuseraft does not store API keys " +
            "in plaintext on disk. Set your provider's API key via an environment variable " +
            "instead (e.g. ANTHROPIC_API_KEY) — see docs/security.md#api-key-storage.");

    public Task DeleteAsync() => Task.CompletedTask;
}
