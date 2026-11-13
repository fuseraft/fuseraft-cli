using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Cli.Protocol;
using fuseraft.Core.Models.Config;
using fuseraft.Core.Subagents;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Context;
using fuseraft.Infrastructure.Storage;

namespace fuseraft.Cli.Commands;

public sealed class SettingsSetSettings : CommandSettings
{
    [CommandArgument(0, "[key]")]
    [Description("Dotted setting key, e.g. provider.modelId, sampling.temperature, repl.noBanner. Run without arguments to list valid keys.")]
    public string Key { get; set; } = string.Empty;

    [CommandArgument(1, "[value]")]
    [Description("New value. Pass an empty string (\"\") to clear a value back to its default.")]
    public string? Value { get; set; }

    [CommandOption("--json")]
    [Description("Print the updated setting, or the error, as one JSON object on stdout (see docs/protocol.md).")]
    public bool Json { get; set; }
}

/// <summary>
/// Sets one field of the global <c>~/.fuseraft/config</c> file by dotted key. Creates the file
/// (with everything else left at its default) if it doesn't exist yet — a non-interactive
/// alternative to the REPL's <c>/provider setup</c> wizard, useful for scripting/CI.
/// </summary>
public sealed class SettingsSetCommand : Command<SettingsSetSettings>
{
    public override int Execute(CommandContext context, SettingsSetSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Key))
        {
            if (settings.Json) return WireProtocol.WriteError("A setting key is required. Run fuseraft settings show --json to list them.");
            PrintValidKeys();
            return 0;
        }

        var key   = settings.Key.Trim();
        var value = settings.Value ?? string.Empty;

        var (existing, _) = UserConfigStore.Load();
        var config = existing ?? new UserConfig();

        var error = key.Equals("provider.apiKey", StringComparison.OrdinalIgnoreCase)
            ? "API keys are never stored in this file. Set FUSERAFT_API_KEY and run fuseraft keychain --set instead."
            : Apply(config, key, value);

        if (error is not null)
        {
            if (settings.Json) return WireProtocol.WriteError(error);
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(error)}");
            if (SettingsCatalog.Find(key) is null) PrintValidKeys();
            return 1;
        }

        UserConfigStore.Save(config);
        if (settings.Json) return WireProtocol.WriteItem(WireSetting.From(SettingsCatalog.Find(key)!, config));
        var displayValue = string.IsNullOrEmpty(value) ? "[dim](cleared)[/]" : Markup.Escape(value);
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(key)} = {displayValue}  [dim](saved to {Markup.Escape(UserConfigStore.ConfigPath)})[/]");
        return 0;
    }

    /// <summary>Sets <paramref name="key"/> on <paramref name="config"/>; the error to show, or null once it's set.</summary>
    internal static string? Apply(UserConfig config, string key, string value)
    {
        if (SettingsCatalog.Find(key) is not { } info)
            return $"Unknown setting key: {key}";
        if (CheckTyped(info, value) is { } typeError)
            return typeError;

        return key.ToLowerInvariant() switch
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
            "repl.theme"               => Assign(() => config.Repl.Theme = string.IsNullOrWhiteSpace(value) ? null : value.Trim()),
            "repl.dock"                => string.IsNullOrWhiteSpace(value) || value.Trim().ToLowerInvariant() is "top" or "bottom"
                ? Assign(() => config.Repl.Dock = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant())
                : "repl.dock must be top or bottom.",
            "repl.bell"                => AssignBool(v => config.Repl.Bell = v, value),
            "repl.autotitle"           => AssignBool(v => config.Repl.AutoTitle = v, value),
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
            "repl.maxtodonudges"             => AssignIntRange(v => config.Repl.MaxTodoNudges = v, value, 0, ReplLimits.TodoNudgesMax),
            "repl.maxturninputtokens"        => AssignIntRange(v => config.Repl.MaxTurnInputTokens = v, value, ReplLimits.TurnInputTokensMin, ReplLimits.TurnInputTokensMax),
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
            "files.trackedfiles"       => AssignIntRange(v => config.Files = (config.Files ?? new FilesConfig()) with { TrackedFiles = v }, value, 0, FilesConfig.MaxTrackedFiles),
            "skills.searchdepth"       => AssignIntRange(v => config.Skills = (config.Skills ?? new SkillsConfig()) with { SearchDepth = v }, value, 1, SkillsConfig.MaxSearchDepth),

            "memory.autosave"          => AssignBool(v => UpdateMemory(config, m => m with { AutoSave = v }), value),
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

            _ => $"Unknown setting key: {key}",
        };
    }

    // The catalog's range and choices hold for every key, so a client showing them can rely on them.
    private static string? CheckTyped(SettingInfo info, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return info.Clearable ? null : $"{info.Key} can't be cleared; set it to {info.Default ?? "a value"} instead.";
        var v = raw.Trim();
        switch (info.Kind)
        {
            case SettingKind.Integer when !long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || OutOfRange(info, n):
                return $"Expected an integer{RangeText(info)}, got '{raw}'.";
            case SettingKind.Number when !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || OutOfRange(info, d):
                return $"Expected a number{RangeText(info)}, got '{raw}'.";
            case SettingKind.Choice when info.Choices is { } choices && !choices.Contains(v, StringComparer.OrdinalIgnoreCase):
                return $"{info.Key} must be one of: {string.Join(", ", choices)}.";
            default:
                return null;
        }
    }

    private static bool OutOfRange(SettingInfo info, double v) => v < info.Min || v > info.Max;

    private static string RangeText(SettingInfo info) => (info.Min, info.Max) switch
    {
        ({ } min, { } max) => $" from {Fmt(min)} to {Fmt(max)}",
        ({ } min, null)    => $" >= {Fmt(min)}",
        (null, { } max)    => $" <= {Fmt(max)}",
        _                  => "",
    };

    private static string Fmt(double v) => v.ToString(CultureInfo.InvariantCulture);

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
        foreach (var info in SettingsCatalog.All)
        {
            var hints = new List<string>();
            if (info.Kind == SettingKind.Bool) hints.Add("true/false");
            if (info.Choices is { } choices) hints.Add(string.Join(" or ", choices));
            if (RangeText(info) is { Length: > 0 } range) hints.Add(range.Trim());
            if (info.Default is { } d) hints.Add($"default {d}");
            if (info.Clearable) hints.Add("\"\" to reset");
            var hint = hints.Count > 0 ? $" ({string.Join("; ", hints)})" : "";
            AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(info.Key),-34}[/] [dim]{Markup.Escape(info.Description + hint)}[/]");
        }
    }
}
