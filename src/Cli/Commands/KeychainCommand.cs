using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Storage;

namespace fuseraft.Cli.Commands;

public sealed class KeychainSettings : CommandSettings
{
    [CommandOption("--set")]
    [Description("Read FUSERAFT_API_KEY from the environment and store it in the OS keychain.")]
    public bool Set { get; set; }

    [CommandOption("--get")]
    [Description("Read the API key from the OS keychain and write it to stdout. Exits 1 if no key is stored.")]
    public bool Get { get; set; }

    [CommandOption("--delete")]
    [Description("Remove the API key from the OS keychain.")]
    public bool Delete { get; set; }

    [CommandOption("--endpoint <URL>")]
    [Description("The provider endpoint the key is for. Defaults to the one in ~/.fuseraft/config.")]
    public string? Endpoint { get; set; }

    public override ValidationResult Validate() =>
        new[] { Set, Get, Delete }.Count(f => f) > 1
            ? ValidationResult.Error("Pass only one of --set, --get, --delete.")
            : ValidationResult.Success();
}

/// <summary>
/// Manages the fuseraft API key in the OS keychain (Windows Credential Manager, macOS Keychain,
/// or secret-tool on Linux). Designed for bidirectional sync with the VS Code extension.
/// Keys are kept per endpoint, so each one is only ever sent to the server it was saved for.
/// </summary>
public sealed class KeychainCommand : AsyncCommand<KeychainSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context, KeychainSettings settings, CancellationToken cancellationToken)
    {
        var store = ApiKeyStoreFactory.Create();

        // Without --endpoint the key is the configured provider's, which is also the only one a
        // key saved before keys were scoped can belong to.
        var explicitEndpoint = !string.IsNullOrWhiteSpace(settings.Endpoint);
        var (userCfg, _) = UserConfigStore.Load();
        var endpoint = explicitEndpoint
            ? settings.Endpoint!.Trim()
            : userCfg is null ? string.Empty : KeyStorePersistence.EndpointOf(userCfg);
        var origin = ProviderKeys.Origin(endpoint);
        if (origin.Length == 0)
        {
            AnsiConsole.MarkupLine(explicitEndpoint
                ? $"[red]✗ '{Markup.Escape(endpoint)}' isn't an http(s) URL.[/]"
                : "[red]✗ No provider endpoint is configured. Pass --endpoint <URL>, or run fuseraft repl to set one up.[/]");
            return 1;
        }
        var adoptLegacy = !explicitEndpoint
            || origin == ProviderKeys.Origin(userCfg is null ? null : KeyStorePersistence.EndpointOf(userCfg));

        if (settings.Set)
        {
            var key = Environment.GetEnvironmentVariable("FUSERAFT_API_KEY");
            if (string.IsNullOrWhiteSpace(key))
            {
                AnsiConsole.MarkupLine("[red]✗ FUSERAFT_API_KEY environment variable is not set.[/]");
                return 1;
            }
            try
            {
                await ProviderKeys.StoreAsync(store, endpoint, key.Trim());
            }
            catch (KeyStoreUnavailableException ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[dim]API key for {Markup.Escape(origin)} stored in {Markup.Escape(store.StoreName)}.[/]");
            return 0;
        }

        if (settings.Get)
        {
            var key = await ProviderKeys.RetrieveAsync(store, endpoint, adoptLegacy);
            if (string.IsNullOrEmpty(key)) return 1;
            Console.Write(key);
            return 0;
        }

        if (settings.Delete)
        {
            await ProviderKeys.DeleteAsync(store, endpoint);
            AnsiConsole.MarkupLine($"[dim]No API key is stored for {Markup.Escape(origin)} now.[/]");
            return 0;
        }

        // No flags: show status.
        var storedKey = await ProviderKeys.RetrieveAsync(store, endpoint, adoptLegacy);
        if (string.IsNullOrEmpty(storedKey))
            AnsiConsole.MarkupLine($"[yellow]No API key for {Markup.Escape(origin)} stored in {Markup.Escape(store.StoreName)}.[/]");
        else
            AnsiConsole.MarkupLine($"[green]✓ API key for {Markup.Escape(origin)} is stored in {Markup.Escape(store.StoreName)}.[/]");
        return 0;
    }
}
