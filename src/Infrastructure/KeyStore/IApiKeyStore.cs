namespace fuseraft.Infrastructure.KeyStore;

public interface IApiKeyStore
{
    string StoreName { get; }
    bool IsAvailable { get; }

    /// <summary>
    /// <paramref name="account"/> selects which entry to operate on within this store. The
    /// default account holds the user's provider API key; other callers (e.g. per-MCP-server
    /// OAuth token caching) pass a distinct account name so entries don't collide.
    /// </summary>
    Task<string?> RetrieveAsync(string account = "default");
    Task StoreAsync(string apiKey, string account = "default");
    Task DeleteAsync(string account = "default");
}
