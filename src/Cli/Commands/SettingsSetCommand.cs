using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models.Config;
using fuseraft.Core.Subagents;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Storage;

namespace fuseraft.Cli.Commands;

public sealed class SettingsSetSettings : CommandSettings
{
    [CommandArgument(0, "<key>")]
    [Description("Dotted setting key, e.g. provider.modelId, sampling.temperature, repl.noBanner. Run without arguments to list valid keys.")]
    public string Key { get; set; } = string.Empty;

    [CommandArgument(1, "[value]")]
    [Description("New value. Pass an empty string (\"\") to clear a value back to its default.")]
    public string? Value { get; set; }
}

/// <summary>
/// Sets one field of the global <c>~/.fuseraft/config</c> file by dotted key. Creates the file
/// (with everything else left at its default) if it doesn't exist yet — a non-interactive
/// alternative to the REPL's <c>/provider setup</c> wizard, useful for scripting/CI.
/// </summary>
public sealed class SettingsSetCommand : Command<SettingsSetSettings>
{
    // (key, description) — printed on an unknown key or when Key is empty.
    private static readonly (string Key, string Description)[] ValidKeys =
    [
        ("provider.modelId",        "Model ID, e.g. claude-sonnet-4-6"),
        ("provider.endpoint",       "Provider base URL"),
        ("provider.type",           "Provider identifier, e.g. openai, anthropic, ollama"),
        ("provider.apiKeyEnvVar",   "Env var name to read the API key from"),
        ("provider.requestTimeoutSeconds",    "Whole-request timeout for model calls, 30-7200 (default 1200; Ollama keeps its own 100 s unless set), or \"\" to reset"),
        ("provider.streamIdleTimeoutSeconds", "Seconds a streaming reply may go without content before it is treated as stalled, 30-3600 (default 300), or \"\" to reset"),
        ("provider.maxRetries",     "Retries of a transient HTTP failure (429/5xx/network) per model call, 0-10 (default 3); setting it also turns off the SDK's own stacked retries, so 0 means one attempt. \"\" resets"),
        ("sampling.temperature",    "0.0-2.0, or \"\" to clear"),
        ("sampling.topP",           "0.0-1.0, or \"\" to clear"),
        ("sampling.seed",           "Integer, or \"\" to clear"),
        ("sampling.maxOutputTokens","Integer, or \"\" to clear"),
        ("repl.contextBudget",      "Token budget override, or \"\" to clear"),
        ("repl.noBanner",           "true/false"),
        ("repl.verbose",            "true/false"),
        ("repl.safeMode",           "true/false — engage /safe-mode at startup"),
        ("repl.yolo",               "true/false — start every session as if --yolo were passed: no HITL prompts, no sandbox (default false)"),
        ("repl.autoCompact",        "true/false — auto-compact at 75% context instead of only warning"),
        ("repl.resumeReplayTurns",  "Integer >= 0 — recent turns to re-display when a session is resumed (0 disables; default 3)"),
        ("repl.hitlAutoApproveReadOnly", "true/false — in HITL mode, skip the y/N prompt for provably read-only shell commands (default false)"),
        ("repl.autoCompactThreshold",     "Context fraction at which the REPL warns/auto-compacts, 0.5-0.95 (default 0.75), or \"\" to reset"),
        ("repl.compactPreserveTailRatio", "Context fraction kept verbatim as recent turns when compacting, 0.05-0.5 (default 0.2), or \"\" to reset"),
        ("repl.maxConsecutiveToolFailures","Consecutive failing tool calls that end a turn, 2-50 (default 3), or \"\" to reset"),
        ("repl.maxIdenticalToolCalls",    "Consecutive identical tool calls that end a turn, 3-50 (default 5), or \"\" to reset"),
        ("repl.warnIdenticalToolCalls",   "Identical calls in a row at which the model is nudged, 2-49 and below the cutoff (default 3), or \"\" to reset"),
        ("repl.maxStreamRetries",         "Automatic retries of a stream that dropped mid-response, 0-5 (default 2), or \"\" to reset"),
        ("repl.plugins",            "Comma-separated plugin list, e.g. Scratchpad,Http"),
        ("telemetry.otlpEndpoint",  "OTLP endpoint URL, or \"\" to disable"),
        ("telemetry.serviceName",   "Requires telemetry.otlpEndpoint to already be set"),
        ("skillCuration.enabled",   "true/false"),
        ("memory.model",            "Model ID for the end-of-session memory-extraction call (e.g. a cheap model), or \"\" to use the main chat model"),
        ("memory.provider",         "Provider for memory.model (openai, anthropic, ...), or \"\" to auto-detect"),
        ("memory.endpoint",         "Base URL for memory.model; reuses the main provider's API key unless memory.apiKeyEnvVar is set"),
        ("memory.apiKeyEnvVar",     "Env var holding the API key for memory.model"),
        ("subagent.model",          "Model ID for /explore, /locate, /delegate subagents (e.g. a cheap model), or \"\" to use the main chat model"),
        ("subagent.provider",       "Provider for subagent.model (openai, anthropic, ...), or \"\" to auto-detect"),
        ("subagent.endpoint",       "Base URL for subagent.model; reuses the main provider's API key unless subagent.apiKeyEnvVar is set"),
        ("subagent.apiKeyEnvVar",   "Env var holding the API key for subagent.model"),
        ("subagent.exploreMaxIterations",  "Round cap for /explore, 1-100 (default 20), or \"\" to reset"),
        ("subagent.delegateMaxIterations", "Round cap for /delegate, 1-100 (default 40), or \"\" to reset"),
        ("subagent.exploreTimeoutMinutes",  "Wall-clock limit for /explore in minutes, 1-240 (default 8), or \"\" to reset"),
        ("subagent.locateTimeoutMinutes",   "Wall-clock limit for /locate in minutes, 1-240 (default 2), or \"\" to reset"),
        ("subagent.delegateTimeoutMinutes", "Wall-clock limit for /delegate in minutes, 1-240 (default 15), or \"\" to reset"),
    ];

    protected override int Execute(CommandContext context, SettingsSetSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Key))
        {
            PrintValidKeys();
            return 1;
        }

        var key   = settings.Key.Trim();
        var value = settings.Value ?? string.Empty;

        var (existing, _) = UserConfigStore.Load();
        var config = existing ?? new UserConfig();

        if (key.Equals("provider.apiKey", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[red]✗[/] API keys are never stored in this file. Set [bold]FUSERAFT_API_KEY[/] and run [bold]fuseraft keychain --set[/] instead.");
            return 1;
        }

        string? error = key.ToLowerInvariant() switch
        {
            "provider.modelid"         => Assign(() => config.ModelId = value),
            "provider.endpoint"        => Assign(() => config.Endpoint = value),
            "provider.type"            => Assign(() => config.Provider = value),
            "provider.apikeyenvvar"    => Assign(() => config.ApiKeyEnvVar = value),
            "provider.requesttimeoutseconds"    => AssignIntRange(v => config.RequestTimeoutSeconds = v, value, TransportOptions.MinRequestTimeoutSeconds, TransportOptions.MaxRequestTimeoutSeconds),
            "provider.streamidletimeoutseconds" => AssignIntRange(v => config.StreamIdleTimeoutSeconds = v, value, TransportOptions.MinStreamIdleTimeoutSeconds, TransportOptions.MaxStreamIdleTimeoutSeconds),
            "provider.maxretries"      => AssignIntRange(v => config.MaxRetries = v, value, 0, TransportOptions.MaxRetriesLimit),

            "sampling.temperature"     => AssignNullableDouble(v => config.Sampling.Temperature = v, value),
            "sampling.topp"            => AssignNullableDouble(v => config.Sampling.TopP = v, value),
            "sampling.seed"            => AssignNullableLong(v => config.Sampling.Seed = v, value),
            "sampling.maxoutputtokens" => AssignNullableInt(v => config.Sampling.MaxOutputTokens = v, value),

            "repl.contextbudget"       => AssignNullableInt(v => config.Repl.ContextBudget = v, value),
            "repl.nobanner"            => AssignBool(v => config.Repl.NoBanner = v, value),
            "repl.verbose"             => AssignBool(v => config.Repl.Verbose = v, value),
            "repl.safemode"            => AssignBool(v => config.Repl.SafeModeDefault = v, value),
            "repl.yolo"                => AssignBool(v => config.Repl.Yolo = v, value),
            "repl.autocompact"         => AssignBool(v => config.Repl.AutoCompact = v, value),
            "repl.resumereplayturns"   => AssignNonNegativeInt(v => config.Repl.ResumeReplayTurns = v, value),
            "repl.hitlautoapprovereadonly" => AssignBool(v => config.Repl.HitlAutoApproveReadOnly = v, value),
            "repl.autocompactthreshold"      => AssignDoubleRange(v => config.Repl.AutoCompactThreshold = v, value, ReplLimits.AutoCompactThresholdMin, ReplLimits.AutoCompactThresholdMax),
            "repl.compactpreservetailratio"  => AssignDoubleRange(v => config.Repl.CompactPreserveTailRatio = v, value, ReplLimits.PreserveTailRatioMin, ReplLimits.PreserveTailRatioMax),
            "repl.maxconsecutivetoolfailures" => AssignIntRange(v => config.Repl.MaxConsecutiveToolFailures = v, value, ReplLimits.ToolFailuresMin, ReplLimits.ToolFailuresMax),
            "repl.maxidenticaltoolcalls"     => AssignIntRange(v => config.Repl.MaxIdenticalToolCalls = v, value, ReplLimits.IdenticalCallsMin, ReplLimits.IdenticalCallsMax),
            "repl.warnidenticaltoolcalls"    => AssignIntRange(v => config.Repl.WarnIdenticalToolCalls = v, value, ReplLimits.WarnIdenticalCallsMin, ReplLimits.IdenticalCallsMax - 1),
            "repl.maxstreamretries"          => AssignIntRange(v => config.Repl.MaxStreamRetries = v, value, 0, ReplLimits.StreamRetriesMax),
            "repl.plugins"             => Assign(() => config.Repl.Plugins = value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()),

            "telemetry.otlpendpoint"   => Assign(() => config.Telemetry = string.IsNullOrWhiteSpace(value)
                ? null
                : (config.Telemetry ?? new TelemetryConfig()) with { OtlpEndpoint = value }),
            "telemetry.servicename"    => config.Telemetry is null
                ? "telemetry.serviceName requires telemetry.otlpEndpoint to be set first."
                : Assign(() => config.Telemetry = config.Telemetry with { ServiceName = string.IsNullOrWhiteSpace(value) ? null : value }),

            "skillcuration.enabled"    => AssignBool(v => config.SkillCuration = (config.SkillCuration ?? new SkillCurationConfig()) with { Enabled = v }, value),

            "memory.model"             => Assign(() => UpdateMemory(config, m => m with { Model = Blank(value) })),
            "memory.provider"          => Assign(() => UpdateMemory(config, m => m with { Provider = Blank(value) })),
            "memory.endpoint"          => Assign(() => UpdateMemory(config, m => m with { Endpoint = Blank(value) })),
            "memory.apikeyenvvar"      => Assign(() => UpdateMemory(config, m => m with { ApiKeyEnvVar = Blank(value) })),
            "subagent.model"           => Assign(() => UpdateSubagent(config, m => m with { Model = Blank(value) })),
            "subagent.provider"        => Assign(() => UpdateSubagent(config, m => m with { Provider = Blank(value) })),
            "subagent.endpoint"        => Assign(() => UpdateSubagent(config, m => m with { Endpoint = Blank(value) })),
            "subagent.apikeyenvvar"    => Assign(() => UpdateSubagent(config, m => m with { ApiKeyEnvVar = Blank(value) })),
            "subagent.exploremaxiterations"  => AssignIterationCap(v => UpdateSubagent(config, m => m with { ExploreMaxIterations = v }), value),
            "subagent.delegatemaxiterations" => AssignIterationCap(v => UpdateSubagent(config, m => m with { DelegateMaxIterations = v }), value),
            "subagent.exploretimeoutminutes"  => AssignIntRange(v => UpdateSubagent(config, m => m with { ExploreTimeoutMinutes = v }), value, 1, SubagentConfig.MaxTimeoutMinutes),
            "subagent.locatetimeoutminutes"   => AssignIntRange(v => UpdateSubagent(config, m => m with { LocateTimeoutMinutes = v }), value, 1, SubagentConfig.MaxTimeoutMinutes),
            "subagent.delegatetimeoutminutes" => AssignIntRange(v => UpdateSubagent(config, m => m with { DelegateTimeoutMinutes = v }), value, 1, SubagentConfig.MaxTimeoutMinutes),

            _ => "unknown-key",
        };

        if (error == "unknown-key")
        {
            AnsiConsole.MarkupLine($"[red]✗ Unknown setting key:[/] {Markup.Escape(key)}");
            PrintValidKeys();
            return 1;
        }

        if (error is not null)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(error)}");
            return 1;
        }

        UserConfigStore.Save(config);
        var displayValue = string.IsNullOrEmpty(value) ? "[dim](cleared)[/]" : Markup.Escape(value);
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(key)} = {displayValue}  [dim](saved to {Markup.Escape(UserConfigStore.ConfigPath)})[/]");
        return 0;
    }

    private static string? Assign(Action assign)
    {
        assign();
        return null;
    }

    private static string? AssignBool(Action<bool> assign, string raw)
    {
        if (!bool.TryParse(raw, out var v))
            return $"Expected true/false, got '{raw}'.";
        assign(v);
        return null;
    }

    private static string? AssignNonNegativeInt(Action<int> assign, string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < 0)
            return $"Expected an integer >= 0, got '{raw}'.";
        assign(v);
        return null;
    }

    private static string? Blank(string v) => string.IsNullOrWhiteSpace(v) ? null : v;

    // Drops the whole section once its last field is cleared, so an emptied section doesn't linger in the file.
    private static void UpdateMemory(UserConfig c, Func<MemoryExtractionConfig, MemoryExtractionConfig> update)
    {
        var updated = update(c.Memory ?? new MemoryExtractionConfig());
        c.Memory = updated.IsEmpty ? null : updated;
    }

    private static void UpdateSubagent(UserConfig c, Func<SubagentConfig, SubagentConfig> update)
    {
        var updated = update(c.Subagent ?? new SubagentConfig());
        c.Subagent = updated.IsEmpty ? null : updated;
    }

    private static string? AssignIterationCap(Action<int?> assign, string raw) =>
        AssignIntRange(assign, raw, 1, SubagentDefinitionLoader.MaxIterationsCeiling);

    private static string? AssignIntRange(Action<int?> assign, string raw, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(raw)) { assign(null); return null; }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
            return $"Expected an integer from {min} to {max}, got '{raw}'.";
        assign(v);
        return null;
    }

    private static string? AssignDoubleRange(Action<double?> assign, string raw, double min, double max)
    {
        if (string.IsNullOrWhiteSpace(raw)) { assign(null); return null; }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
            return $"Expected a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}, got '{raw}'.";
        assign(v);
        return null;
    }

    private static string? AssignNullableInt(Action<int?> assign, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { assign(null); return null; }
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return $"Expected an integer, got '{raw}'.";
        assign(v);
        return null;
    }

    private static string? AssignNullableLong(Action<long?> assign, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { assign(null); return null; }
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return $"Expected an integer, got '{raw}'.";
        assign(v);
        return null;
    }

    private static string? AssignNullableDouble(Action<double?> assign, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { assign(null); return null; }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return $"Expected a number, got '{raw}'.";
        assign(v);
        return null;
    }

    private static void PrintValidKeys()
    {
        AnsiConsole.MarkupLine("[dim]Valid keys:[/]");
        foreach (var (k, desc) in ValidKeys)
            AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(k),-26}[/] [dim]{Markup.Escape(desc)}[/]");
    }
}
