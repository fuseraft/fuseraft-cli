using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Storage;

namespace fuseraft.Cli.Commands;

/// <summary>
/// Pretty-prints the global <c>~/.fuseraft/config</c> file (provider, sampling, REPL, memory, subagent,
/// telemetry, skill, and MCP server sections) — the global counterpart to <see cref="ShowConfigCommand"/>,
/// which renders a project's <c>orchestration.yaml</c> instead.
/// </summary>
public sealed class SettingsShowCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var (config, _) = UserConfigStore.Load();
        if (config is null)
        {
            AnsiConsole.MarkupLine($"[yellow]No global config yet at[/] [bold]{Markup.Escape(UserConfigStore.ConfigPath)}[/][yellow].[/]");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]fuseraft repl[/] [dim](which walks you through[/] [bold]/provider setup[/][dim]), or[/] [bold]fuseraft settings set <key> <value>[/] [dim]to create one.[/]");
            return 0;
        }

        var keyStore  = ApiKeyStoreFactory.Create();
        var storedKey = await keyStore.RetrieveAsync();

        // Keys match `fuseraft settings set` so any row can be copied straight into it; rows without a
        // section prefix (API key, MCP servers, ...) aren't settable that way.
        var table = new Table().Border(TableBorder.None).HideHeaders()
            .AddColumn(new TableColumn("Key").PadLeft(2).PadRight(3))
            .AddColumn(new TableColumn("Value"));

        void Section(string title)
        {
            if (table.Rows.Count > 0) table.AddEmptyRow();
            table.AddRow(new Markup($"[bold]{Markup.Escape(title)}[/]"), Text.Empty);
        }
        void Row(string key, string value)
        {
            var dot = key.IndexOf('.');
            var keyMarkup = dot < 0
                ? Markup.Escape(key)
                : $"[dim]{Markup.Escape(key[..(dot + 1)])}[/]{Markup.Escape(key[(dot + 1)..])}";
            table.AddRow(new Markup("  " + keyMarkup), new Markup(value));
        }

        static string Str(string? v, string fallback) => string.IsNullOrEmpty(v) ? Dim(fallback) : Markup.Escape(v);
        static string Default(string shown) => Dim($"{shown} (default)");
        static string Dim(string text) => $"[dim]{Markup.Escape(text)}[/]";
        static string OnOff(bool on) => on ? "[green]on[/]" : "[dim]off[/]";
        string Tuned<T>(T? set, T fallback, Func<T, string> format) where T : struct =>
            set is { } v ? Markup.Escape(format(v)) : Default(format(fallback));

        Section("Provider");
        Row("provider.modelId",      Str(config.ModelId, "(none)"));
        Row("provider.endpoint",     Str(config.Endpoint, "(auto-detected)"));
        Row("provider.type",         Str(config.Provider, "(auto-detected)"));
        Row("provider.apiKeyEnvVar", Str(config.ApiKeyEnvVar, "(none)"));
        Row("API key", string.IsNullOrEmpty(storedKey)
            ? Dim("not stored — read from the environment, if set")
            : $"[green]stored[/] [dim]in {Markup.Escape(keyStore.StoreName)}[/]");
        Row("provider.requestTimeoutSeconds",    Tuned(config.RequestTimeoutSeconds,    TransportOptions.DefaultRequestTimeoutSeconds,    v => $"{v}s"));
        Row("provider.streamIdleTimeoutSeconds", Tuned(config.StreamIdleTimeoutSeconds, TransportOptions.DefaultStreamIdleTimeoutSeconds, v => $"{v}s"));
        Row("provider.maxRetries",               Tuned(config.MaxRetries,               TransportOptions.DefaultMaxRetries,               v => v.ToString()));

        Section("Sampling");
        Row("sampling.temperature",     config.Sampling.Temperature?.ToString("0.00") ?? Dim("(provider default)"));
        Row("sampling.topP",            config.Sampling.TopP?.ToString("0.00")        ?? Dim("(provider default)"));
        Row("sampling.seed",            config.Sampling.Seed?.ToString()              ?? Dim("(provider default)"));
        Row("sampling.maxOutputTokens", config.Sampling.MaxOutputTokens?.ToString()   ?? Dim("(provider default)"));

        var repl   = config.Repl;
        var limits = ReplLimits.From(repl);
        Section("REPL");
        Row("repl.contextBudget",  repl.ContextBudget?.ToString() ?? Dim("(heuristic)"));
        Row("repl.theme",          repl.Theme is null ? Dim("default") : Markup.Escape(repl.Theme));
        Row("repl.dock",           repl.Dock is null ? Default("bottom") : Markup.Escape(repl.Dock));
        Row("repl.bell",           OnOff(repl.Bell ?? true));
        Row("repl.autoTitle",      OnOff(repl.AutoTitle ?? true));
        Row("repl.noBanner",       OnOff(repl.NoBanner));
        Row("repl.verbose",        OnOff(repl.Verbose));
        Row("repl.safeMode",       OnOff(repl.SafeModeDefault));
        Row("repl.yolo",           repl.Yolo ? "[yellow]on[/] [dim](no HITL, no sandbox)[/]" : OnOff(false));
        Row("repl.hitlAutoApproveReadOnly", OnOff(repl.HitlAutoApproveReadOnly));
        Row("repl.autoCompact",    OnOff(repl.AutoCompact));
        Row("repl.autoCompactThreshold",       Tuned(repl.AutoCompactThreshold,       limits.AutoCompactThreshold,       v => v.ToString("0.00")));
        Row("repl.compactPreserveTailRatio",   Tuned(repl.CompactPreserveTailRatio,   limits.PreserveTailRatio,          v => v.ToString("0.00")));
        Row("repl.maxConsecutiveToolFailures", Tuned(repl.MaxConsecutiveToolFailures, limits.MaxConsecutiveToolFailures, v => v.ToString()));
        Row("repl.maxIdenticalToolCalls",      Tuned(repl.MaxIdenticalToolCalls,      limits.MaxIdenticalToolCalls,      v => v.ToString()));
        Row("repl.warnIdenticalToolCalls",     Tuned(repl.WarnIdenticalToolCalls,     limits.WarnIdenticalToolCalls,     v => v.ToString()));
        Row("repl.maxStreamRetries",           Tuned(repl.MaxStreamRetries,           limits.MaxStreamRetries,           v => v.ToString()));
        Row("repl.maxTodoNudges",              Tuned(repl.MaxTodoNudges,              limits.MaxTodoNudges,              v => v.ToString()));
        Row("repl.maxTurnInputTokens",         Tuned(repl.MaxTurnInputTokens,         limits.MaxTurnInputTokens,         v => v.ToString("N0")));
        Row("repl.resumeReplayTurns", repl.ResumeReplayTurns > 0
            ? $"last {repl.ResumeReplayTurns} turn{(repl.ResumeReplayTurns == 1 ? "" : "s")}"
            : OnOff(false));
        Row("repl.plugins", repl.Plugins.Count > 0 ? Markup.Escape(string.Join(", ", repl.Plugins)) : Dim("(none)"));

        string Conn(string? provider, string? endpoint, string? keyVar) =>
            string.Join(", ", new[] { provider, endpoint, keyVar is null ? null : $"key from {keyVar}" }
                .Where(v => !string.IsNullOrEmpty(v)).Select(v => Markup.Escape(v!))) is { Length: > 0 } c
                ? c : Dim("(auto-detected, else main provider)");

        var memory = config.Memory;
        Section("Memory");
        Row("memory.autoSave", OnOff(memory?.AutoSave == true));
        Row("memory.model",    Str(memory?.Model, "(main chat model)"));
        Row("connection",      Conn(memory?.Provider, memory?.Endpoint, memory?.ApiKeyEnvVar));

        var sub = config.Subagent;
        Section("Subagents");
        Row("subagent.model", Str(sub?.Model, "(main chat model)"));
        Row("connection",     Conn(sub?.Provider, sub?.Endpoint, sub?.ApiKeyEnvVar));
        Row("subagent.exploreMaxIterations",   Tuned(sub?.ExploreMaxIterations,   20, v => $"{v} rounds"));
        Row("subagent.delegateMaxIterations",  Tuned(sub?.DelegateMaxIterations,  40, v => $"{v} rounds"));
        Row("subagent.exploreTimeoutMinutes",  Tuned(sub?.ExploreTimeoutMinutes,  8,  v => $"{v} min"));
        Row("subagent.locateTimeoutMinutes",   Tuned(sub?.LocateTimeoutMinutes,   2,  v => $"{v} min"));
        Row("subagent.delegateTimeoutMinutes", Tuned(sub?.DelegateTimeoutMinutes, 15, v => $"{v} min"));

        Section("Telemetry");
        if (config.Telemetry is { } tel)
        {
            Row("telemetry.otlpEndpoint", Markup.Escape(tel.OtlpEndpoint));
            Row("telemetry.serviceName",  Str(tel.ServiceName, "(orchestration name)"));
        }
        else
        {
            Row("telemetry.otlpEndpoint", Dim("(none — telemetry off)"));
        }

        var sc = config.SkillCuration;
        Section("Skills");
        Row("skills.searchDepth",    Tuned(config.Skills?.SearchDepth, SkillsConfig.DefaultSearchDepth, v => v.ToString()));
        Row("skillCuration.enabled", OnOff(sc?.Enabled == true));
        Row("curation min turns",    (sc?.MinTurns ?? 5).ToString());
        Row("curation model",        Str(sc?.Model, "(first agent's model)"));

        Section("MCP servers");
        Row("servers", config.McpServers.Count > 0
            ? Markup.Escape(string.Join(", ", config.McpServers.Select(s => s.Name)))
            : Dim("(none — add with /mcp add in the REPL)"));

        AnsiConsole.Write(new Rule($"[bold]Global config[/] [dim]{Markup.Escape(UserConfigStore.ConfigPath)}[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Change a value with[/] fuseraft settings set <key> <value>[dim]; pass \"\" to reset it. Run[/] fuseraft settings set [dim]alone to list every key.[/]");
        return 0;
    }
}
