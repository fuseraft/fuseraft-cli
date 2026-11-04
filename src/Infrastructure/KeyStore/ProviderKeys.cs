namespace fuseraft.Infrastructure.KeyStore;

/// <summary>
/// Provider API keys in the OS keychain, one per endpoint origin, so a key is only ever sent to the server it was saved for.
/// </summary>
public static class ProviderKeys
{
    /// <summary>The single shared account keys were stored under before they were scoped to an endpoint.</summary>
    internal const string LegacyAccount = "default";

    private const string AccountPrefix = "provider:";

    /// <summary><c>scheme://host:port</c> of an endpoint, lower-cased; empty when it isn't an absolute http(s) URL.</summary>
    public static string Origin(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return string.Empty;
        return $"{uri.Scheme}://{uri.Host}:{uri.Port}".ToLowerInvariant();
    }

    public static string AccountFor(string origin) => AccountPrefix + origin;

    /// <summary>
    /// The key saved for <paramref name="endpoint"/>'s origin. With <paramref name="adoptLegacy"/>, an unscoped key from
    /// before scoping is moved to this origin when it has none — pass it only for the endpoint the user's config names.
    /// </summary>
    public static async Task<string?> RetrieveAsync(IApiKeyStore store, string? endpoint, bool adoptLegacy = false)
    {
        var origin = Origin(endpoint);
        if (origin.Length == 0) return null;

        var key = await store.RetrieveAsync(AccountFor(origin));
        if (!string.IsNullOrEmpty(key) || !adoptLegacy) return key;

        // Nothing records which provider the legacy key was for; the configured endpoint is the one it was being sent to.
        var legacy = await store.RetrieveAsync(LegacyAccount);
        if (string.IsNullOrEmpty(legacy)) return null;
        await store.StoreAsync(legacy, AccountFor(origin));
        await store.DeleteAsync(LegacyAccount);
        return legacy;
    }

    public static Task StoreAsync(IApiKeyStore store, string? endpoint, string apiKey)
    {
        var origin = Origin(endpoint);
        if (origin.Length == 0)
            throw new ArgumentException($"Can't save an API key for '{endpoint}': it isn't an http(s) URL.", nameof(endpoint));
        return store.StoreAsync(apiKey, AccountFor(origin));
    }

    public static Task DeleteAsync(IApiKeyStore store, string? endpoint)
    {
        var origin = Origin(endpoint);
        return origin.Length == 0 ? Task.CompletedTask : store.DeleteAsync(AccountFor(origin));
    }
}
