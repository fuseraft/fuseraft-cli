using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary><see cref="ProviderKeys"/>: provider API keys kept per endpoint origin, so a key only goes to the server it was saved for.</summary>
public sealed class ProviderKeysTests
{
    [Theory]
    [InlineData("https://api.x.ai/v1", "https://api.x.ai:443")]
    [InlineData("https://API.X.AI/v1/", "https://api.x.ai:443")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434")]
    [InlineData("http://localhost:11434", "http://localhost:11434")]
    [InlineData("http://gpu-box", "http://gpu-box:80")]
    [InlineData(" https://my-server.example.com:8443/openai ", "https://my-server.example.com:8443")]
    public void Origin_IsSchemeHostAndPort(string endpoint, string expected) =>
        Assert.Equal(expected, ProviderKeys.Origin(endpoint));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("api.x.ai/v1")]
    [InlineData("file:///etc/passwd")]
    public void Origin_IsEmptyForAnythingButAnHttpUrl(string? endpoint) =>
        Assert.Equal(string.Empty, ProviderKeys.Origin(endpoint));

    [Fact]
    public async Task AKey_IsOnlyReturnedForItsOwnOrigin()
    {
        var store = new FakeApiKeyStore();
        await ProviderKeys.StoreAsync(store, "https://api.x.ai/v1", "xai-key");

        Assert.Equal("xai-key", await ProviderKeys.RetrieveAsync(store, "https://api.x.ai/v1/chat"));
        Assert.Null(await ProviderKeys.RetrieveAsync(store, "http://localhost:11434/v1"));
        Assert.Null(await ProviderKeys.RetrieveAsync(store, "https://api.x.ai.evil.example/v1"));
    }

    [Fact]
    public async Task EachOrigin_KeepsItsOwnKey()
    {
        var store = new FakeApiKeyStore();
        await ProviderKeys.StoreAsync(store, "https://api.x.ai/v1", "xai-key");
        await ProviderKeys.StoreAsync(store, "https://api.anthropic.com", "ant-key");

        Assert.Equal("xai-key", await ProviderKeys.RetrieveAsync(store, "https://api.x.ai/v1"));
        Assert.Equal("ant-key", await ProviderKeys.RetrieveAsync(store, "https://api.anthropic.com"));
    }

    [Fact]
    public async Task LegacyKey_IsAdoptedByTheConfiguredEndpoint_Once()
    {
        var store = new FakeApiKeyStore();
        store.Entries[ProviderKeys.LegacyAccount] = "old-key";

        Assert.Equal("old-key", await ProviderKeys.RetrieveAsync(store, "https://api.x.ai/v1", adoptLegacy: true));

        Assert.False(store.Entries.ContainsKey(ProviderKeys.LegacyAccount));
        Assert.Equal("old-key", await ProviderKeys.RetrieveAsync(store, "https://api.x.ai/v1"));
        Assert.Null(await ProviderKeys.RetrieveAsync(store, "http://localhost:11434", adoptLegacy: true));
    }

    [Fact]
    public async Task LegacyKey_IsLeftAlone_WithoutAdoptLegacy()
    {
        var store = new FakeApiKeyStore();
        store.Entries[ProviderKeys.LegacyAccount] = "old-key";

        Assert.Null(await ProviderKeys.RetrieveAsync(store, "http://localhost:11434"));
        Assert.Equal("old-key", store.Entries[ProviderKeys.LegacyAccount]);
    }

    [Fact]
    public async Task LegacyKey_DoesNotReplaceAKeyTheOriginAlreadyHas()
    {
        var store = new FakeApiKeyStore();
        store.Entries[ProviderKeys.LegacyAccount] = "old-key";
        await ProviderKeys.StoreAsync(store, "https://api.x.ai/v1", "new-key");

        Assert.Equal("new-key", await ProviderKeys.RetrieveAsync(store, "https://api.x.ai/v1", adoptLegacy: true));
        Assert.Equal("old-key", store.Entries[ProviderKeys.LegacyAccount]);
    }

    [Fact]
    public async Task Delete_RemovesOnlyThatOriginsKey()
    {
        var store = new FakeApiKeyStore();
        await ProviderKeys.StoreAsync(store, "https://api.x.ai/v1", "xai-key");
        await ProviderKeys.StoreAsync(store, "https://api.anthropic.com", "ant-key");

        await ProviderKeys.DeleteAsync(store, "https://api.x.ai");

        Assert.Null(await ProviderKeys.RetrieveAsync(store, "https://api.x.ai/v1"));
        Assert.Equal("ant-key", await ProviderKeys.RetrieveAsync(store, "https://api.anthropic.com"));
    }

    [Fact]
    public async Task Store_RefusesAnEndpointWithNoOrigin()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => ProviderKeys.StoreAsync(new FakeApiKeyStore(), "", "k"));
        Assert.Contains("isn't an http(s) URL", ex.Message);
    }

    private sealed class FakeApiKeyStore : IApiKeyStore
    {
        public Dictionary<string, string> Entries { get; } = new();

        public string StoreName => "fake";
        public bool IsAvailable => true;

        public Task<string?> RetrieveAsync(string account = "default") =>
            Task.FromResult(Entries.TryGetValue(account, out var v) ? v : null);

        public Task StoreAsync(string apiKey, string account = "default")
        {
            Entries[account] = apiKey;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string account = "default")
        {
            Entries.Remove(account);
            return Task.CompletedTask;
        }
    }
}
