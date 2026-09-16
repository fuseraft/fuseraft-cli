using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using fuseraft.Infrastructure.KeyStore;

namespace fuseraft.Infrastructure.Mcp;

/// <summary>
/// Persists OAuth tokens for one MCP server across CLI process runs, backed by the same OS
/// keychain used for provider API keys (<see cref="IApiKeyStore"/>) — never plaintext on disk.
///
/// <para>
/// Each server gets its own keychain entry, keyed by a sanitized form of its config name plus a
/// short hash of its endpoint URL (so renaming a server's <c>Name</c> in config doesn't silently
/// inherit — or orphan — another server's cached token). Without this, every <c>fuseraft run</c>
/// or <c>fuseraft repl</c> invocation against an OAuth-gated server would need a fresh browser
/// login.
/// </para>
///
/// <para>
/// When no OS keychain is available (<see cref="IApiKeyStore.IsAvailable"/> is <c>false</c>, or a
/// store throws <see cref="KeyStoreUnavailableException"/> on write), tokens are kept in-memory
/// for the current process only — the login flow still works, it just repeats next run. This
/// mirrors how fuseraft already degrades provider API key storage rather than ever writing a
/// secret to disk in plaintext.
/// </para>
/// </summary>
public sealed class McpOAuthTokenCache(
    string serverName,
    string serverUrl,
    IApiKeyStore keyStore,
    ILogger? logger = null) : ITokenCache
{
    private readonly string _account = BuildAccountName(serverName, serverUrl);
    private TokenContainer? _memoryFallback;
    private bool _warnedUnavailable;

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        if (!keyStore.IsAvailable)
            return _memoryFallback;

        string? json;
        try
        {
            json = await keyStore.RetrieveAsync(_account);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not read cached OAuth token for MCP server '{Name}' from {Store}.",
                serverName, keyStore.StoreName);
            return _memoryFallback;
        }

        if (string.IsNullOrEmpty(json))
            return _memoryFallback;

        try
        {
            return JsonSerializer.Deserialize<TokenContainer>(json);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Cached OAuth token for MCP server '{Name}' was corrupted — discarding.", serverName);
            return null;
        }
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        _memoryFallback = tokens;

        if (!keyStore.IsAvailable)
            return;

        var json = JsonSerializer.Serialize(tokens);
        try
        {
            await keyStore.StoreAsync(json, _account);
        }
        catch (KeyStoreUnavailableException) when (!_warnedUnavailable)
        {
            _warnedUnavailable = true;
            logger?.LogWarning(
                "No OS keychain is available to persist the OAuth token for MCP server '{Name}' — " +
                "you'll need to log in again next session.", serverName);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not persist OAuth token for MCP server '{Name}' to {Store}.",
                serverName, keyStore.StoreName);
        }
    }

    /// <summary>
    /// Deletes the cached token for the given server, if any. Used by e.g. <c>/mcp remove</c> or
    /// a future <c>/mcp logout</c> so a revoked or stale token doesn't linger in the keychain.
    /// </summary>
    public static Task LogoutAsync(string serverName, string serverUrl, IApiKeyStore keyStore) =>
        keyStore.DeleteAsync(BuildAccountName(serverName, serverUrl));

    // Keychain account names must stay simple (no spaces/quotes — SecretToolKeyStore splices
    // this straight into a shell-ish command line) and must not collide across servers that
    // happen to share a display name, so mix in a short hash of the endpoint URL.
    private static string BuildAccountName(string serverName, string serverUrl)
    {
        var sanitized = new string(serverName
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray())
            .Trim('-');
        if (sanitized.Length == 0) sanitized = "server";

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverUrl)))[..8].ToLowerInvariant();
        return $"mcp-oauth-{sanitized}-{hash}";
    }
}
