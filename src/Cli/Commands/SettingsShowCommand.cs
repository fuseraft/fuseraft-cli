using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Infrastructure;
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
        provider.AddRow("Request timeout",     config.RequestTimeoutSeconds is { } rt ? $"{rt}s" : $"[dim](default {TransportOptions.DefaultRequestTimeoutSeconds}s)[/]");
        provider.AddRow("Stream idle timeout", config.StreamIdleTimeoutSeconds is { } st ? $"{st}s" : $"[dim](default {TransportOptions.DefaultStreamIdleTimeoutSeconds}s)[/]");
        provider.AddRow("Max retries",         config.MaxRetries is { } mr ? mr.ToString() : $"[dim](default {TransportOptions.DefaultMaxRetries})[/]");
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
        repl.AddRow("Yolo",           config.Repl.Yolo            ? "[yellow]on[/] [dim](no HITL, no sandbox)[/]" : "[dim]off[/]");
        repl.AddRow("Auto-compact",   config.Repl.AutoCompact     ? "[green]on[/]" : "[dim]off[/]");
        repl.AddRow("HITL auto-approve read-only", config.Repl.HitlAutoApproveReadOnly ? "[green]on[/]" : "[dim]off[/]");
        var limits = ReplLimits.From(config.Repl);
        string Tuned(bool set, string shown) => set ? shown : $"[dim](default {shown})[/]";
        repl.AddRow("Auto-compact threshold",  Tuned(config.Repl.AutoCompactThreshold is not null,    limits.AutoCompactThreshold.ToString("0.00")));
        repl.AddRow("Compact preserved tail",  Tuned(config.Repl.CompactPreserveTailRatio is not null, limits.PreserveTailRatio.ToString("0.00")));
        repl.AddRow("Max tool failures",       Tuned(config.Repl.MaxConsecutiveToolFailures is not null, limits.MaxConsecutiveToolFailures.ToString()));
        repl.AddRow("Max identical tool calls", Tuned(config.Repl.MaxIdenticalToolCalls is not null,   limits.MaxIdenticalToolCalls.ToString()));
        repl.AddRow("Identical-call warning",  Tuned(config.Repl.WarnIdenticalToolCalls is not null,   limits.WarnIdenticalToolCalls.ToString()));
        repl.AddRow("Max stream retries",      Tuned(config.Repl.MaxStreamRetries is not null,         limits.MaxStreamRetries.ToString()));
        repl.AddRow("Resume replay",  config.Repl.ResumeReplayTurns > 0
            ? $"last {config.Repl.ResumeReplayTurns} turn{(config.Repl.ResumeReplayTurns == 1 ? "" : "s")}"
            : "[dim]off[/]");
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

        var modelOverrides = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Model overrides[/]")
            .AddColumn("Field").AddColumn("Value");
        string Conn(string? provider, string? endpoint, string? keyVar) =>
            string.Join(", ", new[] { provider, endpoint, keyVar is null ? null : $"key from {keyVar}" }
                .Where(v => !string.IsNullOrEmpty(v)).Select(v => Markup.Escape(v!)));
        modelOverrides.AddRow("Memory extraction", string.IsNullOrEmpty(config.Memory?.Model)
            ? "[dim](main chat model)[/]" : Markup.Escape(config.Memory!.Model!));
        modelOverrides.AddRow("  connection", Conn(config.Memory?.Provider, config.Memory?.Endpoint, config.Memory?.ApiKeyEnvVar) is { Length: > 0 } mc
            ? mc : "[dim](auto-detected from model ID, else main provider)[/]");
        modelOverrides.AddRow("Subagents", string.IsNullOrEmpty(config.Subagent?.Model)
            ? "[dim](main chat model)[/]" : Markup.Escape(config.Subagent!.Model!));
        modelOverrides.AddRow("  connection", Conn(config.Subagent?.Provider, config.Subagent?.Endpoint, config.Subagent?.ApiKeyEnvVar) is { Length: > 0 } sc2
            ? sc2 : "[dim](auto-detected from model ID, else main provider)[/]");
        AnsiConsole.Write(modelOverrides);
        AnsiConsole.WriteLine();

        var subagentLimits = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Subagent limits[/]")
            .AddColumn("Field").AddColumn("Value");
        subagentLimits.AddRow("/explore rounds",   config.Subagent?.ExploreMaxIterations is { } e ? e.ToString() : "[dim](default 20)[/]");
        subagentLimits.AddRow("/delegate rounds",  config.Subagent?.DelegateMaxIterations is { } d ? d.ToString() : "[dim](default 40)[/]");
        subagentLimits.AddRow("/explore timeout",  config.Subagent?.ExploreTimeoutMinutes is { } et ? $"{et} min" : "[dim](default 8 min)[/]");
        subagentLimits.AddRow("/locate timeout",   config.Subagent?.LocateTimeoutMinutes is { } lt ? $"{lt} min" : "[dim](default 2 min)[/]");
        subagentLimits.AddRow("/delegate timeout", config.Subagent?.DelegateTimeoutMinutes is { } dt ? $"{dt} min" : "[dim](default 15 min)[/]");
        AnsiConsole.Write(subagentLimits);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine(config.McpServers.Count > 0
            ? $"[bold]MCP servers:[/] {Markup.Escape(string.Join(", ", config.McpServers.Select(s => s.Name)))}"
            : "[bold]MCP servers:[/] [dim](none — add with /mcp add in the REPL)[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(UserConfigStore.ConfigPath)}[/]");
        return 0;
    }
}
