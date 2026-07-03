using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Infrastructure.KeyStore;

namespace fuseraft.Cli.Commands;

public sealed class KeychainSettings : CommandSettings
{
    [CommandOption("--set")]
    [Description("Read FUSERAFT_API_KEY from the environment and store it in the OS keychain.")]
    public bool Set { get; set; }

    [CommandOption("--get")]
    [Description("Read the API key from the OS keychain and write it to stdout. Exits 1 if no key is stored.")]
    public bool Get { get; set; }
}

/// <summary>
/// Manages the fuseraft API key in the OS keychain (Windows Credential Manager, macOS Keychain,
/// or secret-tool on Linux). Designed for bidirectional sync with the VS Code extension.
/// </summary>
public sealed class KeychainCommand : AsyncCommand<KeychainSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, KeychainSettings settings, CancellationToken cancellationToken)
    {
        var store = ApiKeyStoreFactory.Create();

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
                await store.StoreAsync(key.Trim());
            }
            catch (KeyStoreUnavailableException ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[dim]API key stored in {Markup.Escape(store.StoreName)}.[/]");
            return 0;
        }

        if (settings.Get)
        {
            var key = await store.RetrieveAsync();
            if (string.IsNullOrEmpty(key)) return 1;
            Console.Write(key);
            return 0;
        }

        // No flags: show status.
        var storedKey = await store.RetrieveAsync();
        if (string.IsNullOrEmpty(storedKey))
            AnsiConsole.MarkupLine($"[yellow]No API key stored in {Markup.Escape(store.StoreName)}.[/]");
        else
            AnsiConsole.MarkupLine($"[green]✓ API key is stored in {Markup.Escape(store.StoreName)}.[/]");
        return 0;
    }
}
