using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Storage;

namespace fuseraft.Cli.Commands;

/// <summary>
/// Pretty-prints the global <c>~/.fuseraft/config</c> file (provider, sampling, REPL, telemetry,
/// skill curation, and MCP server sections) — the global counterpart to <see cref="ShowConfigCommand"/>,
/// which renders a project's <c>orchestration.yaml</c> instead.
/// </summary>
public sealed class SettingsShowCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var (config, _) = UserConfigStore.Load();
        if (config is null)
        {
            AnsiConsole.MarkupLine($"[yellow]No global config yet at[/] [bold]{Markup.Escape(UserConfigStore.ConfigPath)}[/][yellow].[/]");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]fuseraft repl[/] [dim](which walks you through[/] [bold]/provider setup[/][dim]), or[/] [bold]fuseraft settings set <key> <value>[/] [dim]to create one.[/]");
            return 0;
        }

        AnsiConsole.Write(new Rule("[bold]Global config[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var keyStore  = ApiKeyStoreFactory.Create();
        var storedKey = await keyStore.RetrieveAsync();
        var keyDisplay = string.IsNullOrEmpty(storedKey)
            ? "[dim](not set — from environment or unconfigured)[/]"
            : $"[green]stored[/] [dim]in {Markup.Escape(keyStore.StoreName)}[/]";

        var provider = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Provider[/]")
            .AddColumn("Field").AddColumn("Value");
        provider.AddRow("Model",          string.IsNullOrEmpty(config.ModelId)      ? "[dim](none)[/]" : Markup.Escape(config.ModelId));
        provider.AddRow("Endpoint",       string.IsNullOrEmpty(config.Endpoint)     ? "[dim](auto-detected)[/]" : Markup.Escape(config.Endpoint));
        provider.AddRow("Type",           string.IsNullOrEmpty(config.Provider)     ? "[dim](auto-detected)[/]" : Markup.Escape(config.Provider));
        provider.AddRow("API key env var", string.IsNullOrEmpty(config.ApiKeyEnvVar) ? "[dim](none)[/]" : Markup.Escape(config.ApiKeyEnvVar));
        provider.AddRow("API key",        keyDisplay);
        AnsiConsole.Write(provider);
        AnsiConsole.WriteLine();

        var sampling = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Sampling defaults[/]")
            .AddColumn("Field").AddColumn("Value");
        sampling.AddRow("Temperature",       config.Sampling.Temperature?.ToString("0.00")   ?? "[dim](provider default)[/]");
        sampling.AddRow("Top-p",             config.Sampling.TopP?.ToString("0.00")          ?? "[dim](provider default)[/]");
        sampling.AddRow("Seed",              config.Sampling.Seed?.ToString()                ?? "[dim](provider default)[/]");
        sampling.AddRow("Max output tokens", config.Sampling.MaxOutputTokens?.ToString()      ?? "[dim](provider default)[/]");
        AnsiConsole.Write(sampling);
        AnsiConsole.WriteLine();

        var repl = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]REPL defaults[/]")
            .AddColumn("Field").AddColumn("Value");
        repl.AddRow("Context budget", config.Repl.ContextBudget?.ToString() ?? "[dim](heuristic)[/]");
        repl.AddRow("No banner",      config.Repl.NoBanner       ? "[green]on[/]" : "[dim]off[/]");
        repl.AddRow("Verbose",        config.Repl.Verbose        ? "[green]on[/]" : "[dim]off[/]");
        repl.AddRow("Safe mode",      config.Repl.SafeModeDefault ? "[green]on[/]" : "[dim]off[/]");
        repl.AddRow("Plugins",        config.Repl.Plugins.Count > 0 ? Markup.Escape(string.Join(", ", config.Repl.Plugins)) : "[dim](none)[/]");
        AnsiConsole.Write(repl);
        AnsiConsole.WriteLine();

        var telemetry = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Telemetry default[/]")
            .AddColumn("Field").AddColumn("Value");
        if (config.Telemetry is { } tel)
        {
            telemetry.AddRow("OTLP endpoint", Markup.Escape(tel.OtlpEndpoint));
            telemetry.AddRow("Service name",  string.IsNullOrEmpty(tel.ServiceName) ? "[dim](orchestration name)[/]" : Markup.Escape(tel.ServiceName));
        }
        else
        {
            telemetry.AddRow("Status", "[dim]disabled — no global default set[/]");
        }
        AnsiConsole.Write(telemetry);
        AnsiConsole.WriteLine();

        var skills = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Skill curation[/]")
            .AddColumn("Field").AddColumn("Value");
        var sc = config.SkillCuration;
        skills.AddRow("Enabled",   sc?.Enabled == true ? "[green]on[/]" : "[dim]off[/]");
        skills.AddRow("Min turns", (sc?.MinTurns ?? 5).ToString());
        skills.AddRow("Model",     string.IsNullOrEmpty(sc?.Model) ? "[dim](first agent's model)[/]" : Markup.Escape(sc!.Model!));
        AnsiConsole.Write(skills);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine(config.McpServers.Count > 0
            ? $"[bold]MCP servers:[/] {Markup.Escape(string.Join(", ", config.McpServers.Select(s => s.Name)))}"
            : "[bold]MCP servers:[/] [dim](none — add with /mcp add in the REPL)[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(UserConfigStore.ConfigPath)}[/]");
        return 0;
    }
}
