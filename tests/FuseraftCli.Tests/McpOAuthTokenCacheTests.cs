using ModelContextProtocol.Authentication;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Mcp;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="McpOAuthTokenCache"/> is fuseraft's <see cref="ITokenCache"/> adapter over the same
/// <see cref="IApiKeyStore"/> abstraction used for provider API keys — these tests use an
/// in-memory fake store instead of a real OS keychain.
/// </summary>
public sealed class McpOAuthTokenCacheTests
{
    private static TokenContainer MakeTokens(string accessToken = "at-123") => new()
    {
        TokenType   = "Bearer",
        AccessToken = accessToken,
        ObtainedAt  = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task StoreThenGet_RoundTripsThroughKeyStore()
    {
        var store = new FakeApiKeyStore();
        var cache = new McpOAuthTokenCache("MyServer", "https://example.com/mcp", store);

        await cache.StoreTokensAsync(MakeTokens("at-abc"), CancellationToken.None);
        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("at-abc", loaded!.AccessToken);
        Assert.Single(store.Entries); // persisted under one keychain account, not the raw token cache directly
    }

    [Fact]
    public async Task DifferentServers_UseDistinctKeychainAccounts()
    {
        var store = new FakeApiKeyStore();
        var cacheA = new McpOAuthTokenCache("ServerA", "https://a.example.com/mcp", store);
        var cacheB = new McpOAuthTokenCache("ServerB", "https://b.example.com/mcp", store);

        await cacheA.StoreTokensAsync(MakeTokens("token-a"), CancellationToken.None);
        await cacheB.StoreTokensAsync(MakeTokens("token-b"), CancellationToken.None);

        Assert.Equal("token-a", (await cacheA.GetTokensAsync(CancellationToken.None))!.AccessToken);
        Assert.Equal("token-b", (await cacheB.GetTokensAsync(CancellationToken.None))!.AccessToken);
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact]
    public async Task SameNameDifferentUrl_DoesNotCollide()
    {
        // Two servers that happen to share a display Name (e.g. re-added under the same alias
        // after pointing it at a different deployment) must not share a cached token.
        var store = new FakeApiKeyStore();
        var cacheOld = new McpOAuthTokenCache("Remote", "https://old.example.com/mcp", store);
        var cacheNew = new McpOAuthTokenCache("Remote", "https://new.example.com/mcp", store);

        await cacheOld.StoreTokensAsync(MakeTokens("old-token"), CancellationToken.None);

        var loadedForNew = await cacheNew.GetTokensAsync(CancellationToken.None);
        Assert.Null(loadedForNew);
    }

    [Fact]
    public async Task KeyStoreUnavailable_DegradesToInMemoryOnly_DoesNotThrow()
    {
        var store = new FakeApiKeyStore { Available = false };
        var cache = new McpOAuthTokenCache("MyServer", "https://example.com/mcp", store);

        await cache.StoreTokensAsync(MakeTokens("at-memory-only"), CancellationToken.None);
        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("at-memory-only", loaded!.AccessToken);
        Assert.Empty(store.Entries); // never touched the underlying store
    }

    [Fact]
    public async Task KeyStoreThrowsUnavailable_StoreDoesNotThrow_MemoryFallbackStillWorks()
    {
        var store = new FakeApiKeyStore { ThrowUnavailableOnStore = true };
        var cache = new McpOAuthTokenCache("MyServer", "https://example.com/mcp", store);

        // Must not propagate KeyStoreUnavailableException — same "keep it in memory for this
        // process" degradation the exception's own doc comment describes for API keys.
        await cache.StoreTokensAsync(MakeTokens("at-degraded"), CancellationToken.None);
        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("at-degraded", loaded!.AccessToken);
    }

    [Fact]
    public async Task GetTokens_NoStoredEntry_ReturnsNull()
    {
        var store = new FakeApiKeyStore();
        var cache = new McpOAuthTokenCache("MyServer", "https://example.com/mcp", store);

        Assert.Null(await cache.GetTokensAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LogoutAsync_DeletesTheServersEntry()
    {
        var store = new FakeApiKeyStore();
        var cache = new McpOAuthTokenCache("MyServer", "https://example.com/mcp", store);
        await cache.StoreTokensAsync(MakeTokens(), CancellationToken.None);
        Assert.Single(store.Entries);

        await McpOAuthTokenCache.LogoutAsync("MyServer", "https://example.com/mcp", store);

        Assert.Empty(store.Entries);
    }

    private sealed class FakeApiKeyStore : IApiKeyStore
    {
        public Dictionary<string, string> Entries { get; } = new();
        public bool Available { get; set; } = true;
        public bool ThrowUnavailableOnStore { get; set; }

        public string StoreName => "fake";
        public bool IsAvailable => Available;

        public Task<string?> RetrieveAsync(string account = "default") =>
            Task.FromResult(Entries.TryGetValue(account, out var v) ? v : null);

        public Task StoreAsync(string apiKey, string account = "default")
        {
            if (ThrowUnavailableOnStore)
                throw new KeyStoreUnavailableException("fake store unavailable");
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
