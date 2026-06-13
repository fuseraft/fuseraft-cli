using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands.Log;

internal static class EventLogViewer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static Task<int> RenderAsync(
        string path,
        int? last,
        string? sessionFilter,
        string? eventFilter,
        CancellationToken ct) =>
        RenderAsync([path], last, sessionFilter, eventFilter, ct);

    internal static async Task<int> RenderAsync(
        IReadOnlyList<string> paths,
        int? last,
        string? sessionFilter,
        string? eventFilter,
        CancellationToken ct)
    {
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No event log found.[/]");
            if (paths.Count == 1)
                AnsiConsole.MarkupLine($"[dim]Expected path: {Markup.Escape(paths[0])}[/]");
            return 0;
        }

        var entries = new List<EventLogEntry>();
        foreach (var path in existing)
        {
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
        var logLabel = existing.Count == 1 ? existing[0] : $"{existing.Count} session log(s)";
        AnsiConsole.MarkupLine($"[dim]log: {Markup.Escape(logLabel)}[/]");

        return 0;
    }

    private static string ColorizeEvent(string eventType) => eventType switch
    {
        EventTypes.SessionStart                          => $"[cyan]{EventTypes.SessionStart}[/]",
        EventTypes.SessionEnd                            => $"[cyan]{EventTypes.SessionEnd}[/]",
        EventTypes.SessionError                          => $"[red]{EventTypes.SessionError}[/]",
        EventTypes.CircuitBreakerOpen                    => $"[red]{EventTypes.CircuitBreakerOpen}[/]",
        EventTypes.ToolBlocked                           => $"[yellow]{EventTypes.ToolBlocked}[/]",
        EventTypes.ValidationFail                        => $"[yellow]{EventTypes.ValidationFail}[/]",
        EventTypes.HitlEscalation                        => $"[yellow]{EventTypes.HitlEscalation}[/]",
        EventTypes.SkillCurationComplete                 => $"[green]{EventTypes.SkillCurationComplete}[/]",
        EventTypes.SkillCurationStart                    => $"[dim]{EventTypes.SkillCurationStart}[/]",
        EventTypes.TurnStart or EventTypes.TurnEnd       => $"[dim]{Markup.Escape(eventType)}[/]",
        EventTypes.Command                               => $"[dim]{EventTypes.Command}[/]",
        _                                                => Markup.Escape(eventType),
    };

    private static string SummarizePayload(string? eventType, JsonElement? payload)
    {
        if (payload is not { } p) return string.Empty;

        try
        {
            return eventType switch
            {
                EventTypes.Command =>
                    Get(p, "command") is { } cmd
                        ? $"[dim]{Markup.Escape(Truncate(cmd, 60))}[/]"
                        : string.Empty,

                EventTypes.SkillCurationComplete =>
                    (Get(p, "outcome"), Get(p, "slug")) is ({ } outcome, { } slug)
                        ? $"[dim]{Markup.Escape(outcome)}  {Markup.Escape(slug)}[/]"
                        : Get(p, "outcome") is { } o
                            ? $"[dim]{Markup.Escape(o)}[/]"
                            : string.Empty,

                EventTypes.SessionError =>
                    Get(p, "error") is { } err
                        ? $"[dim red]{Markup.Escape(Truncate(err, 80))}[/]"
                        : string.Empty,

                EventTypes.ToolBlocked =>
                    Get(p, "tool") is { } tool
                        ? $"[dim]{Markup.Escape(tool)}[/]"
                        : string.Empty,

                EventTypes.ValidationFail =>
                    Get(p, "validator") is { } v
                        ? $"[dim]{Markup.Escape(v)}[/]"
                        : string.Empty,

                EventTypes.SessionStart =>
                    Get(p, "model") is { } model
                        ? $"[dim]{Markup.Escape(Truncate(model, 30))}[/]"
                        : string.Empty,

                EventTypes.TurnEnd =>
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
