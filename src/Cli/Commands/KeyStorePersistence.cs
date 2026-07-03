using Spectre.Console;
using fuseraft.Infrastructure.KeyStore;

namespace fuseraft.Cli.Commands;

/// <summary>
/// Shared helper for call sites that persist a freshly entered or migrated API key into the
/// OS keychain. fuseraft never stores API keys in plaintext on disk — when no keychain is
/// available this prints guidance and lets the caller continue with the key held in memory
/// for the current process only.
/// </summary>
internal static class KeyStorePersistence
{
    public static async Task<bool> TryStoreAsync(IApiKeyStore keyStore, string apiKey)
    {
        try
        {
            await keyStore.StoreAsync(apiKey);
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
