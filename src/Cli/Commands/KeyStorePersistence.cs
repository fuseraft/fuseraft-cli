using Spectre.Console;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace fuseraft.Cli.Commands;

/// <summary>
/// Shared helper for call sites that load or persist the provider API key in the OS keychain.
/// Keys are kept per endpoint (see <see cref="ProviderKeys"/>). fuseraft never stores API keys
/// in plaintext on disk — when no keychain is available this prints guidance and lets the caller
/// continue with the key held in memory for the current process only.
/// </summary>
internal static class KeyStorePersistence
{
    /// <summary>The endpoint <paramref name="cfg"/>'s model is called at, which its key is saved under.</summary>
    public static string EndpointOf(UserConfig cfg) =>
        ChatClientFactory.EndpointFor(cfg.ModelId, cfg.Provider, cfg.Endpoint);

    /// <summary>Fills <paramref name="cfg"/>'s key from the keychain entry for its endpoint.</summary>
    public static async Task LoadAsync(IApiKeyStore keyStore, UserConfig cfg)
    {
        var endpoint = EndpointOf(cfg);
        cfg.ApiKey       = await ProviderKeys.RetrieveAsync(keyStore, endpoint, adoptLegacy: true) ?? string.Empty;
        cfg.ApiKeyOrigin = ProviderKeys.Origin(endpoint);
    }

    /// <summary>Saves <paramref name="apiKey"/> for <paramref name="cfg"/>'s endpoint and marks it as that endpoint's key.</summary>
    public static async Task<bool> TryStoreAsync(IApiKeyStore keyStore, UserConfig cfg, string apiKey)
    {
        var endpoint = EndpointOf(cfg);
        cfg.ApiKeyOrigin = ProviderKeys.Origin(endpoint);
        if (cfg.ApiKeyOrigin.Length == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Couldn't tell which server model '{Markup.Escape(cfg.ModelId)}' is called at, so the API key wasn't saved.[/]");
            AnsiConsole.MarkupLine("[dim]Using this key for the current session only — set an endpoint to have it remembered.[/]");
            return false;
        }
        try
        {
            await ProviderKeys.StoreAsync(keyStore, endpoint, apiKey);
            return true;
        }
        catch (KeyStoreUnavailableException ex)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine("[dim]Using this key for the current session only — it will not be remembered.[/]");
            return false;
        }
    }
}
