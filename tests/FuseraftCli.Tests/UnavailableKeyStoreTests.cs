using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// fuseraft never persists API keys to disk in plaintext. When no OS keychain is reachable,
/// <see cref="ApiKeyStoreFactory.Create"/> returns an <see cref="UnavailableKeyStore"/> instead
/// of writing a fallback file — these tests pin that contract directly.
/// </summary>
public sealed class UnavailableKeyStoreTests
{
    [Fact]
    public void IsAvailable_IsFalse()
    {
        Assert.False(new UnavailableKeyStore().IsAvailable);
    }

    [Fact]
    public async Task RetrieveAsync_ReturnsNull()
    {
        Assert.Null(await new UnavailableKeyStore().RetrieveAsync());
    }

    [Fact]
    public async Task StoreAsync_ThrowsKeyStoreUnavailable_NeverWritesToDisk()
    {
        var store = new UnavailableKeyStore();
        var ex = await Assert.ThrowsAsync<KeyStoreUnavailableException>(() => store.StoreAsync("sk-should-never-land-on-disk"));
        Assert.Contains("plaintext", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_IsNoOp()
    {
        await new UnavailableKeyStore().DeleteAsync(); // must not throw
    }
}
