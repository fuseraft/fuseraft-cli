using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core.Models.Config;
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
        ("sampling.temperature",    "0.0-2.0, or \"\" to clear"),
        ("sampling.topP",           "0.0-1.0, or \"\" to clear"),
        ("sampling.seed",           "Integer, or \"\" to clear"),
        ("sampling.maxOutputTokens","Integer, or \"\" to clear"),
        ("repl.contextBudget",      "Token budget override, or \"\" to clear"),
        ("repl.noBanner",           "true/false"),
        ("repl.verbose",            "true/false"),
        ("repl.safeMode",           "true/false — engage /safe-mode at startup"),
        ("repl.autoCompact",        "true/false — auto-compact at 75% context instead of only warning"),
        ("repl.plugins",            "Comma-separated plugin list, e.g. Scratchpad,Http"),
        ("telemetry.otlpEndpoint",  "OTLP endpoint URL, or \"\" to disable"),
        ("telemetry.serviceName",   "Requires telemetry.otlpEndpoint to already be set"),
        ("skillCuration.enabled",   "true/false"),
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

            "sampling.temperature"     => AssignNullableDouble(v => config.Sampling.Temperature = v, value),
            "sampling.topp"            => AssignNullableDouble(v => config.Sampling.TopP = v, value),
            "sampling.seed"            => AssignNullableLong(v => config.Sampling.Seed = v, value),
            "sampling.maxoutputtokens" => AssignNullableInt(v => config.Sampling.MaxOutputTokens = v, value),

            "repl.contextbudget"       => AssignNullableInt(v => config.Repl.ContextBudget = v, value),
            "repl.nobanner"            => AssignBool(v => config.Repl.NoBanner = v, value),
            "repl.verbose"             => AssignBool(v => config.Repl.Verbose = v, value),
            "repl.safemode"            => AssignBool(v => config.Repl.SafeModeDefault = v, value),
            "repl.autocompact"         => AssignBool(v => config.Repl.AutoCompact = v, value),
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
