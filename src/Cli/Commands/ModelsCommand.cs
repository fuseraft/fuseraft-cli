using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Storage;
using fuseraft.Cli;
using fuseraft.Cli.Protocol;

namespace fuseraft.Cli.Commands;

public sealed class ModelsSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Print one JSON object to stdout instead of a list (see docs/protocol.md). Never starts the setup wizard.")]
    public bool Json { get; set; }

    [CommandOption("--provider")]
    [Description("List another connection's models instead of the configured one: openai (any OpenAI-compatible API), anthropic, xai, google, mistral, deepseek or ollama. The key comes from FUSERAFT_API_KEY.")]
    public string? Provider { get; set; }

    [CommandOption("--endpoint")]
    [Description("Provider base URL for that connection. Needed for a custom server; defaults to the provider's public endpoint.")]
    public string? Endpoint { get; set; }
}

public sealed class ModelsCommand : AsyncCommand<ModelsSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ModelsSettings settings, CancellationToken cancellationToken)
    {
        if (ConnectionOverride.From(settings.Provider, settings.Endpoint) is { } connection)
            return await ListConnectionAsync(settings, connection, cancellationToken);

        var keyStore = ApiKeyStoreFactory.Create();
        var (userCfg, legacyKey) = UserConfigStore.Load();

        if (!string.IsNullOrEmpty(legacyKey))
        {
            userCfg!.ApiKey = legacyKey;
            if (await KeyStorePersistence.TryStoreAsync(keyStore, userCfg, legacyKey) && !settings.Json)
                AnsiConsole.MarkupLine($"[dim]API key migrated to {Markup.Escape(keyStore.StoreName)}.[/]");
            UserConfigStore.Save(userCfg);
        }
        else if (userCfg is not null)
        {
            await KeyStorePersistence.LoadAsync(keyStore, userCfg);
        }

        bool pendingSave = false;
        if (userCfg is null || !userCfg.IsConfigured)
        {
            bool isInteractive = !Console.IsInputRedirected && !OrchestratorConfigLoader.VsCodeMode;
            if (settings.Json)
                return WireProtocol.WriteError("fuseraft is not configured. Run 'fuseraft setup' to set an API key.");
            if (!isInteractive)
            {
                AnsiConsole.MarkupLine("[yellow]fuseraft is not configured. Run 'fuseraft setup' to set an API key.[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[dim]No configuration found at[/] [bold]{Markup.Escape(UserConfigStore.ConfigPath)}[/]");
            AnsiConsole.WriteLine();
            string? wizardKey;
            bool selectedFromList;
            (userCfg, wizardKey, selectedFromList) = await ReplFactory.RunSetupWizardAsync(null, userCfg);
            if (userCfg is null || wizardKey is null) return 1;
            if (!string.IsNullOrEmpty(wizardKey))
                await KeyStorePersistence.TryStoreAsync(keyStore, userCfg, wizardKey);
            userCfg.ApiKey = wizardKey;
            if (selectedFromList)
                UserConfigStore.Save(userCfg);
            else
                pendingSave = true;
        }

        var modelConfig = ReplFactory.BuildModelConfig(userCfg.ModelId, userCfg);
        using var factory = new ChatClientFactory();

        ModelConfig resolved;
        try
        {
            resolved = factory.Resolve(modelConfig);
        }
        catch (Exception ex)
        {
            if (settings.Json) return WireProtocol.WriteError($"Could not resolve provider config: {ex.Message}");
            AnsiConsole.MarkupLine($"[red]✗ Could not resolve provider config:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var endpoint = resolved.Endpoint.TrimEnd('/');
        var apiKey = !string.IsNullOrEmpty(resolved.ApiKey)
            ? resolved.ApiKey
            : string.IsNullOrEmpty(resolved.ApiKeyEnvVar)
                ? string.Empty
                : Environment.GetEnvironmentVariable(resolved.ApiKeyEnvVar) ?? string.Empty;

        List<string> modelIds;
        try
        {
            modelIds = await FetchAsync(resolved.Provider, endpoint, apiKey, cancellationToken);
        }
        catch (Exception ex)
        {
            if (settings.Json) return WireProtocol.WriteError(ex.Message);
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (pendingSave)
            UserConfigStore.Save(userCfg);

        return Print(settings, endpoint, modelIds, userCfg.ModelId);
    }

    /// <summary>
    /// A host's connection (fuseraft-web's profile form): the config's key belongs to the config's connection,
    /// so only FUSERAFT_API_KEY is sent, and nothing is read from or saved to the config.
    /// </summary>
    private static async Task<int> ListConnectionAsync(ModelsSettings settings, ConnectionOverride connection, CancellationToken ct)
    {
        var provider = connection.Provider ?? "openai";
        var endpoint = (connection.Endpoint ?? ChatClientFactory.DefaultEndpoint(provider))?.TrimEnd('/');
        if (endpoint is null)
        {
            var message = $"Provider '{provider}' has no default endpoint. Pass --endpoint.";
            if (settings.Json) return WireProtocol.WriteError(message);
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(message)}[/]");
            return 1;
        }

        List<string> modelIds;
        try
        {
            modelIds = await FetchAsync(provider, endpoint, Environment.GetEnvironmentVariable("FUSERAFT_API_KEY") ?? string.Empty, ct);
        }
        catch (Exception ex)
        {
            if (settings.Json) return WireProtocol.WriteError(ex.Message);
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        return Print(settings, endpoint, modelIds, currentId: null);
    }

    private static Task<List<string>> FetchAsync(string provider, string endpoint, string apiKey, CancellationToken ct) =>
        provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
            ? ProviderModelsClient.FetchAnthropicAsync(endpoint, apiKey, ct)
            : ProviderModelsClient.FetchAsync(endpoint, apiKey, provider.Equals("ollama", StringComparison.OrdinalIgnoreCase), ct);

    private static int Print(ModelsSettings settings, string endpoint, List<string> modelIds, string? currentId)
    {
        bool IsCurrent(string m) => m.Equals(currentId, StringComparison.OrdinalIgnoreCase);

        if (settings.Json)
            return WireProtocol.WriteList(modelIds.Select(m => new WireModel(m, IsCurrent(m))));

        AnsiConsole.MarkupLine($"  [dim]Available models from[/] [bold]{Markup.Escape(endpoint)}[/] [dim]({modelIds.Count})[/]");
        AnsiConsole.WriteLine();
        foreach (var m in modelIds)
        {
            if (IsCurrent(m))
                AnsiConsole.MarkupLine($"  [bold green]{Markup.Escape(m)}[/] [dim]← current[/]");
            else
                AnsiConsole.MarkupLine($"  {Markup.Escape(m)}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Run[/] [bold]fuseraft repl --model <id>[/] [dim]to try one, or add[/] [bold]--save[/] [dim]to make it the default.[/]");

        return 0;
    }
}
