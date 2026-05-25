using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;

namespace fuseraft.Cli.Commands.Skills;

// fuseraft skills curation-log

public sealed class SkillsCurationLogSettings : CommandSettings
{
    [CommandOption("-n|--last")]
    [Description("Show only the last N entries. Defaults to all entries.")]
    public int? Last { get; set; }

    [CommandOption("--outcome")]
    [Description("Filter by outcome: created, updated, skipped, no_skill, failed.")]
    public string? Outcome { get; set; }

    [CommandOption("--source")]
    [Description("Filter by source: run, repl.")]
    public string? Source { get; set; }

    [CommandOption("--path")]
    [Description("Path to the curation log file. Defaults to ~/.fuseraft/skill-curation.jsonl.")]
    public string? Path { get; set; }
}

public sealed class SkillsCurationLogCommand : AsyncCommand<SkillsCurationLogSettings>
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    protected override async Task<int> ExecuteAsync(
        CommandContext context, SkillsCurationLogSettings settings, CancellationToken cancellationToken)
    {
        var logPath = !string.IsNullOrWhiteSpace(settings.Path)
            ? FuseraftPaths.ExpandPath(settings.Path)
            : FuseraftPaths.GlobalSkillCurationLog;

        if (!File.Exists(logPath))
        {
            AnsiConsole.MarkupLine("[dim]No curation log found. Run a session with skill curation enabled to generate one.[/]");
            AnsiConsole.MarkupLine($"[dim]Expected path: {Markup.Escape(logPath)}[/]");
            return 0;
        }

        // Parse all lines, skip blanks and malformed entries.
        var entries = new List<CurationLogEntry>();
        await foreach (var line in File.ReadLinesAsync(logPath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<CurationLogEntry>(line, JsonOpts);
                if (entry is not null) entries.Add(entry);
            }
            catch { /* skip malformed lines */ }
        }

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Curation log is empty.[/]");
            return 0;
        }

        // Apply filters.
        if (!string.IsNullOrWhiteSpace(settings.Outcome))
            entries = entries
                .Where(e => e.Outcome.Equals(settings.Outcome.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (!string.IsNullOrWhiteSpace(settings.Source))
            entries = entries
                .Where(e => (e.Source ?? string.Empty).Equals(settings.Source.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No entries match the specified filters.[/]");
            return 0;
        }

        // --last N
        if (settings.Last is > 0)
            entries = entries.TakeLast(settings.Last.Value).ToList();

        // Summary counts (over the full filtered set before --last truncation would be
        // confusing, so count the already-filtered entries that are displayed).
        var counts = entries
            .GroupBy(e => e.Outcome, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key.ToLowerInvariant(), g => g.Count());

        // Table
        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Time[/]"))
            .AddColumn(new TableColumn("[bold]Source[/]"))
            .AddColumn(new TableColumn("[bold]Outcome[/]"))
            .AddColumn(new TableColumn("[bold]Slug[/]"))
            .AddColumn(new TableColumn("[bold]Turns[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Model[/]"))
            .AddColumn(new TableColumn("[bold]Note[/]"));

        foreach (var e in entries)
        {
            var ts = DateTimeOffset.TryParse(e.Ts, out var dto)
                ? dto.ToLocalTime().ToString("MM-dd HH:mm")
                : e.Ts ?? "-";

            var outcomeMarkup = (e.Outcome.ToLowerInvariant()) switch
            {
                "created"  => "[green]created[/]",
                "updated"  => "[cyan]updated[/]",
                "no_skill" => "[dim]no_skill[/]",
                "skipped"  => "[dim]skipped[/]",
                "failed"   => "[red]failed[/]",
                var other  => Markup.Escape(other),
            };

            var note = !string.IsNullOrWhiteSpace(e.FailureReason)
                ? $"[dim]{Markup.Escape(Truncate(e.FailureReason, 60))}[/]"
                : string.Empty;

            table.AddRow(
                $"[dim]{Markup.Escape(ts)}[/]",
                $"[dim]{Markup.Escape(e.Source ?? "-")}[/]",
                outcomeMarkup,
                !string.IsNullOrWhiteSpace(e.Slug) ? Markup.Escape(e.Slug) : "[dim]-[/]",
                e.TurnsDigested.HasValue ? $"[dim]{e.TurnsDigested}[/]" : "[dim]-[/]",
                !string.IsNullOrWhiteSpace(e.Model) ? $"[dim]{Markup.Escape(Truncate(e.Model, 24))}[/]" : "[dim]-[/]",
                note);
        }

        AnsiConsole.Write(table);

        // Summary line
        var parts = new List<string> { $"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}" };
        foreach (var (outcome, count) in counts.OrderBy(k => k.Key))
            parts.Add($"{count} {outcome}");
        AnsiConsole.MarkupLine($"[dim]{string.Join("  ·  ", parts)}[/]");
        AnsiConsole.MarkupLine($"[dim]log: {Markup.Escape(logPath)}[/]");

        return 0;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private sealed class CurationLogEntry
    {
        [JsonPropertyName("ts")]            public string?  Ts            { get; init; }
        [JsonPropertyName("session")]       public string?  Session       { get; init; }
        [JsonPropertyName("source")]        public string?  Source        { get; init; }
        [JsonPropertyName("outcome")]       public string   Outcome       { get; init; } = string.Empty;
        [JsonPropertyName("slug")]          public string?  Slug          { get; init; }
        [JsonPropertyName("path")]          public string?  Path          { get; init; }
        [JsonPropertyName("turns_digested")]public int?     TurnsDigested { get; init; }
        [JsonPropertyName("model")]         public string?  Model         { get; init; }
        [JsonPropertyName("failure_reason")]public string?  FailureReason { get; init; }
    }
}
