using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;

namespace fuseraft.Cli.Commands;

// ── fuseraft log events ───────────────────────────────────────────────────────

public sealed class LogEventsSettings : CommandSettings
{
    [CommandOption("-n|--last")]
    [Description("Show only the last N entries.")]
    public int? Last { get; set; }

    [CommandOption("--session")]
    [Description("Filter by session ID (prefix match).")]
    public string? Session { get; set; }

    [CommandOption("--event")]
    [Description("Filter by event type (e.g. session_error, tool_blocked).")]
    public string? Event { get; set; }

    [CommandOption("--path")]
    [Description("Override the log file path. Defaults to .fuseraft/logs/events.jsonl.")]
    public string? Path { get; set; }
}

public sealed class LogEventsCommand : AsyncCommand<LogEventsSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, LogEventsSettings settings, CancellationToken cancellationToken)
    {
        var path = !string.IsNullOrWhiteSpace(settings.Path)
            ? FuseraftPaths.ExpandPath(settings.Path)
            : System.IO.Path.GetFullPath(FuseraftPaths.LocalEventsLog);

        return await EventLogViewer.RenderAsync(path, settings.Last, settings.Session, settings.Event, cancellationToken);
    }
}

// ── fuseraft log repl ─────────────────────────────────────────────────────────

public sealed class LogReplSettings : CommandSettings
{
    [CommandOption("-n|--last")]
    [Description("Show only the last N entries.")]
    public int? Last { get; set; }

    [CommandOption("--session")]
    [Description("Filter by session ID (prefix match).")]
    public string? Session { get; set; }

    [CommandOption("--event")]
    [Description("Filter by event type (e.g. command, skill_curation_complete).")]
    public string? Event { get; set; }

    [CommandOption("--path")]
    [Description("Override the log file path. Defaults to .fuseraft/logs/repl_events.jsonl.")]
    public string? Path { get; set; }
}

public sealed class LogReplCommand : AsyncCommand<LogReplSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, LogReplSettings settings, CancellationToken cancellationToken)
    {
        var path = !string.IsNullOrWhiteSpace(settings.Path)
            ? FuseraftPaths.ExpandPath(settings.Path)
            : System.IO.Path.GetFullPath(FuseraftPaths.LocalReplEventsLog);

        return await EventLogViewer.RenderAsync(path, settings.Last, settings.Session, settings.Event, cancellationToken);
    }
}

// ── fuseraft log app ──────────────────────────────────────────────────────────

public sealed class LogAppSettings : CommandSettings
{
    [CommandOption("-n|--last")]
    [Description("Show only the last N lines. Defaults to 50.")]
    public int Last { get; set; } = 50;

    [CommandOption("--level")]
    [Description("Filter by log level prefix: inf, wrn, err, dbg.")]
    public string? Level { get; set; }

    [CommandOption("--path")]
    [Description("Override the log file path. Defaults to .fuseraft/logs/app.log.")]
    public string? Path { get; set; }
}

public sealed class LogAppCommand : AsyncCommand<LogAppSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, LogAppSettings settings, CancellationToken cancellationToken)
    {
        var path = !string.IsNullOrWhiteSpace(settings.Path)
            ? FuseraftPaths.ExpandPath(settings.Path)
            : System.IO.Path.GetFullPath(FuseraftPaths.LocalAppLog);

        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine("[dim]No application log found.[/]");
            AnsiConsole.MarkupLine($"[dim]Expected path: {Markup.Escape(path)}[/]");
            return 0;
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);

        // Filter by level if requested (matches Serilog format: [HH:mm:ss LEV])
        if (!string.IsNullOrWhiteSpace(settings.Level))
        {
            var lvl = settings.Level.Trim().ToUpperInvariant();
            lines = lines
                .Where(l => l.Contains($" {lvl}]", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        // Take last N
        if (settings.Last > 0 && lines.Length > settings.Last)
            lines = lines[^settings.Last..];

        if (lines.Length == 0)
        {
            AnsiConsole.MarkupLine("[dim]No matching log lines.[/]");
            return 0;
        }

        foreach (var line in lines)
            AnsiConsole.MarkupLine(ColorizeAppLogLine(line));

        AnsiConsole.MarkupLine($"[dim]{lines.Length} line{(lines.Length == 1 ? "" : "s")}  ·  {Markup.Escape(path)}[/]");
        return 0;
    }

    private static string ColorizeAppLogLine(string line)
    {
        // Serilog format: [HH:mm:ss LEV] Message
        if (line.Length < 15) return Markup.Escape(line);
        if (line.Contains(" ERR]")) return $"[red]{Markup.Escape(line)}[/]";
        if (line.Contains(" WRN]")) return $"[yellow]{Markup.Escape(line)}[/]";
        if (line.Contains(" DBG]")) return $"[dim]{Markup.Escape(line)}[/]";
        return $"[dim]{Markup.Escape(line)}[/]";
    }
}

// ── Shared JSONL event log viewer ─────────────────────────────────────────────

file static class EventLogViewer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static async Task<int> RenderAsync(
        string path,
        int? last,
        string? sessionFilter,
        string? eventFilter,
        CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine("[dim]No event log found.[/]");
            AnsiConsole.MarkupLine($"[dim]Expected path: {Markup.Escape(path)}[/]");
            return 0;
        }

        var entries = new List<EventLogEntry>();
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<EventLogEntry>(line, JsonOpts);
                if (entry is not null) entries.Add(entry);
            }
            catch { /* skip malformed lines */ }
        }

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Event log is empty.[/]");
            return 0;
        }

        // Filters
        if (!string.IsNullOrWhiteSpace(sessionFilter))
            entries = entries
                .Where(e => (e.Session ?? string.Empty)
                    .StartsWith(sessionFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (!string.IsNullOrWhiteSpace(eventFilter))
            entries = entries
                .Where(e => (e.EventType ?? string.Empty)
                    .Equals(eventFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No entries match the specified filters.[/]");
            return 0;
        }

        if (last is > 0)
            entries = entries.TakeLast(last.Value).ToList();

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Time[/]"))
            .AddColumn(new TableColumn("[bold]Session[/]"))
            .AddColumn(new TableColumn("[bold]Agent[/]"))
            .AddColumn(new TableColumn("[bold]Turn[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Event[/]"))
            .AddColumn(new TableColumn("[bold]Details[/]"));

        foreach (var e in entries)
        {
            var ts = DateTimeOffset.TryParse(e.Ts, out var dto)
                ? dto.ToLocalTime().ToString("MM-dd HH:mm:ss")
                : e.Ts ?? "-";

            var sessionShort = e.Session is { Length: > 0 }
                ? Markup.Escape(e.Session.Length > 12 ? e.Session[..12] : e.Session)
                : "[dim]-[/]";

            table.AddRow(
                $"[dim]{Markup.Escape(ts)}[/]",
                $"[dim]{sessionShort}[/]",
                !string.IsNullOrWhiteSpace(e.Agent) ? $"[dim]{Markup.Escape(e.Agent)}[/]" : "[dim]-[/]",
                e.Turn.HasValue ? $"[dim]{e.Turn}[/]" : "[dim]-[/]",
                ColorizeEvent(e.EventType ?? "-"),
                SummarizePayload(e.EventType, e.Payload));
        }

        AnsiConsole.Write(table);

        var eventCounts = entries
            .GroupBy(e => e.EventType ?? "?", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"{g.Count()} {g.Key}");
        AnsiConsole.MarkupLine(
            $"[dim]{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}  ·  {string.Join("  · ", eventCounts)}[/]");
        AnsiConsole.MarkupLine($"[dim]log: {Markup.Escape(path)}[/]");

        return 0;
    }

    private static string ColorizeEvent(string eventType) => eventType switch
    {
        "session_start"             => "[cyan]session_start[/]",
        "session_end"               => "[cyan]session_end[/]",
        "session_error"             => "[red]session_error[/]",
        "circuit_breaker_open"      => "[red]circuit_breaker_open[/]",
        "tool_blocked"              => "[yellow]tool_blocked[/]",
        "validation_fail"           => "[yellow]validation_fail[/]",
        "hitl_escalation"           => "[yellow]hitl_escalation[/]",
        "skill_curation_complete"   => "[green]skill_curation_complete[/]",
        "skill_curation_start"      => "[dim]skill_curation_start[/]",
        "turn_start" or "turn_end"  => $"[dim]{Markup.Escape(eventType)}[/]",
        "command"                   => "[dim]command[/]",
        _                           => Markup.Escape(eventType),
    };

    private static string SummarizePayload(string? eventType, JsonElement? payload)
    {
        if (payload is not { } p) return string.Empty;

        try
        {
            return eventType switch
            {
                "command" =>
                    Get(p, "command") is { } cmd
                        ? $"[dim]{Markup.Escape(Truncate(cmd, 60))}[/]"
                        : string.Empty,

                "skill_curation_complete" =>
                    (Get(p, "outcome"), Get(p, "slug")) is ({ } outcome, { } slug)
                        ? $"[dim]{Markup.Escape(outcome)}  {Markup.Escape(slug)}[/]"
                        : Get(p, "outcome") is { } o
                            ? $"[dim]{Markup.Escape(o)}[/]"
                            : string.Empty,

                "session_error" =>
                    Get(p, "error") is { } err
                        ? $"[dim red]{Markup.Escape(Truncate(err, 80))}[/]"
                        : string.Empty,

                "tool_blocked" =>
                    Get(p, "tool") is { } tool
                        ? $"[dim]{Markup.Escape(tool)}[/]"
                        : string.Empty,

                "validation_fail" =>
                    Get(p, "validator") is { } v
                        ? $"[dim]{Markup.Escape(v)}[/]"
                        : string.Empty,

                "session_start" =>
                    Get(p, "model") is { } model
                        ? $"[dim]{Markup.Escape(Truncate(model, 30))}[/]"
                        : string.Empty,

                "turn_end" =>
                    Get(p, "agent") is { } agent
                        ? $"[dim]{Markup.Escape(agent)}[/]"
                        : string.Empty,

                _ => string.Empty,
            };
        }
        catch { return string.Empty; }
    }

    private static string? Get(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private sealed class EventLogEntry
    {
        [JsonPropertyName("ts")]         public string?      Ts        { get; init; }
        [JsonPropertyName("session")]    public string?      Session   { get; init; }
        [JsonPropertyName("agent")]      public string?      Agent     { get; init; }
        [JsonPropertyName("turn")]       public int?         Turn      { get; init; }
        [JsonPropertyName("event_type")] public string?      EventType { get; init; }
        [JsonPropertyName("payload")]    public JsonElement? Payload   { get; init; }
    }
}
