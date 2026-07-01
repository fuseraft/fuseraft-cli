using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Storage;
using fuseraft.Cli;

namespace fuseraft.Cli.Commands;

public sealed class ModelsCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var keyStore = ApiKeyStoreFactory.Create();
        var (userCfg, legacyKey) = UserConfigStore.Load();

        if (!string.IsNullOrEmpty(legacyKey))
        {
            await keyStore.StoreAsync(legacyKey);
            userCfg!.ApiKey = legacyKey;
            UserConfigStore.Save(userCfg);
            AnsiConsole.MarkupLine($"[dim]API key migrated to {Markup.Escape(keyStore.StoreName)}.[/]");
        }
        else if (userCfg is not null)
        {
            userCfg.ApiKey = await keyStore.RetrieveAsync() ?? string.Empty;
        }

        bool pendingSave = false;
        if (userCfg is null || !userCfg.IsConfigured)
        {
            bool isInteractive = !Console.IsInputRedirected && !OrchestratorBuilder.VsCodeMode;
            if (!isInteractive)
            {
                AnsiConsole.MarkupLine("[yellow]fuseraft is not configured. Run 'fuseraft setup' to set an API key.[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[dim]No configuration found at[/] [bold]{Markup.Escape(UserConfigStore.ConfigPath)}[/]");
            AnsiConsole.WriteLine();
            string? wizardKey;
            (userCfg, wizardKey) = await ReplFactory.RunSetupWizardAsync(null, userCfg);
            if (userCfg is null || wizardKey is null) return 1;
            if (!string.IsNullOrEmpty(wizardKey))
                await keyStore.StoreAsync(wizardKey);
            userCfg.ApiKey = wizardKey;
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
            AnsiConsole.MarkupLine($"[red]✗ Could not resolve provider config:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var endpoint = resolved.Endpoint.TrimEnd('/');
        var apiKey = !string.IsNullOrEmpty(resolved.ApiKey)
            ? resolved.ApiKey
            : string.IsNullOrEmpty(resolved.ApiKeyEnvVar)
                ? string.Empty
                : Environment.GetEnvironmentVariable(resolved.ApiKeyEnvVar) ?? string.Empty;

        bool isOllama = resolved.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);

        List<string> modelIds;
        try
        {
            modelIds = await ProviderModelsClient.FetchAsync(endpoint, apiKey, isOllama, cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (pendingSave)
            UserConfigStore.Save(userCfg);

        AnsiConsole.MarkupLine($"  [dim]Available models from[/] [bold]{Markup.Escape(endpoint)}[/] [dim]({modelIds.Count})[/]");
        AnsiConsole.WriteLine();
        foreach (var m in modelIds)
        {
            var isCurrent = m.Equals(userCfg.ModelId, StringComparison.OrdinalIgnoreCase);
            if (isCurrent)
                AnsiConsole.MarkupLine($"  [bold green]{Markup.Escape(m)}[/] [dim]← current[/]");
            else
                AnsiConsole.MarkupLine($"  {Markup.Escape(m)}");
        }

        return 0;
    }
}
