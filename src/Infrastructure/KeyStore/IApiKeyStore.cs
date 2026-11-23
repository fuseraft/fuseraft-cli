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

    /// <summary>
    /// Every account this store holds a fuseraft entry under, so all of them can be found without knowing their names.
    /// Throws when the store can't be read, so "couldn't look" is never mistaken for "nothing there".
    /// </summary>
    Task<IReadOnlyList<string>> ListAccountsAsync();
}
