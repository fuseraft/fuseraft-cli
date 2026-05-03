namespace fuseraft.Infrastructure.KeyStore;

public interface IApiKeyStore
{
    string StoreName { get; }
    bool IsAvailable { get; }
    Task<string?> RetrieveAsync();
    Task StoreAsync(string apiKey);
    Task DeleteAsync();
}
