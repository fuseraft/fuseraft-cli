using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands;

// fuseraft skills add <source>

public sealed class SkillsAddSettings : CommandSettings
{
    [CommandArgument(0, "<source>")]
    [Description("Path to a skill directory (containing SKILL.md) or directly to a SKILL.md file.")]
    public string Source { get; set; } = string.Empty;
}

public sealed class SkillsAddCommand : AsyncCommand<SkillsAddSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SkillsAddSettings settings, CancellationToken cancellationToken)
    {
        var sourcePath = FuseraftPaths.ExpandPath(settings.Source);

        string skillMdPath;
        if (File.Exists(sourcePath) && Path.GetFileName(sourcePath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            skillMdPath = sourcePath;
        else if (Directory.Exists(sourcePath))
        {
            skillMdPath = Path.Combine(sourcePath, "SKILL.md");
            if (!File.Exists(skillMdPath))
            {
                AnsiConsole.MarkupLine($"[red]✗ No SKILL.md found in {Markup.Escape(sourcePath)}[/]");
                return 1;
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ Path not found: {Markup.Escape(settings.Source)}[/]");
            return 1;
        }

        var content = await File.ReadAllTextAsync(skillMdPath, cancellationToken);
        var slug    = SkillsHelpers.ExtractSlug(content)
                      ?? SkillsHelpers.ToSlug(Path.GetFileName(Path.GetDirectoryName(skillMdPath)) ?? "skill");

        if (string.IsNullOrWhiteSpace(slug))
        {
            AnsiConsole.MarkupLine("[red]✗ Could not derive a slug. Add a 'name:' field to the SKILL.md frontmatter.[/]");
            return 1;
        }

        var destDir  = Path.Combine(FuseraftPaths.GlobalSkills, slug);
        var destPath = Path.Combine(destDir, "SKILL.md");
        var isUpdate = File.Exists(destPath);

        Directory.CreateDirectory(destDir);
        await File.WriteAllTextAsync(destPath, content, cancellationToken);

        await using var index = new SkillIndex();
        try
        {
            await index.IndexAsync(slug, destPath, content, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("e_sqlite3") || ex.Message.Contains("SQLite"))
        {
            AnsiConsole.MarkupLine($"[red]✗ Skill index unavailable:[/] {Markup.Escape(ex.Message)}");
            // The skill file was already written; report partial success so the user isn't blocked.
            var verb2 = isUpdate ? "Updated" : "Added";
            AnsiConsole.MarkupLine($"[green]✓[/] {verb2} [bold]{Markup.Escape(slug)}[/] → {Markup.Escape(destPath)} [dim](index skipped)[/]");
            return 0;
        }

        var verb = isUpdate ? "Updated" : "Added";
        AnsiConsole.MarkupLine($"[green]✓[/] {verb} [bold]{Markup.Escape(slug)}[/] → {Markup.Escape(destPath)}");
        return 0;
    }
}

// fuseraft skills list

public sealed class SkillsListSettings : CommandSettings { }

public sealed class SkillsListCommand : AsyncCommand<SkillsListSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SkillsListSettings settings, CancellationToken cancellationToken)
    {
        var root = FuseraftPaths.GlobalSkills;

        if (!Directory.Exists(root))
        {
            AnsiConsole.MarkupLine("[dim]No skills installed. Use [bold]fuseraft skills add <path>[/] to add one.[/]");
            return 0;
        }

        var entries = new List<(string Slug, string Description)>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d))
        {
            var mdPath = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(mdPath)) continue;
            var content = await File.ReadAllTextAsync(mdPath, cancellationToken);
            var slug    = Path.GetFileName(dir);
            var desc    = SkillsHelpers.ExtractDescription(content);
            entries.Add((slug, desc));
        }

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No skills installed. Use [bold]fuseraft skills add <path>[/] to add one.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Slug[/]"))
            .AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var (slug, desc) in entries)
            table.AddRow(Markup.Escape(slug), Markup.Escape(desc));

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{entries.Count} skill(s) in {Markup.Escape(root)}[/]");
        return 0;
    }
}

// fuseraft skills remove <slug>

public sealed class SkillsRemoveSettings : CommandSettings
{
    [CommandArgument(0, "<slug>")]
    [Description("Slug of the skill to remove (as shown by 'fuseraft skills list').")]
    public string Slug { get; set; } = string.Empty;
}

public sealed class SkillsRemoveCommand : AsyncCommand<SkillsRemoveSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SkillsRemoveSettings settings, CancellationToken cancellationToken)
    {
        var slug    = settings.Slug.Trim();
        var destDir = Path.Combine(FuseraftPaths.GlobalSkills, slug);

        if (!Directory.Exists(destDir))
        {
            AnsiConsole.MarkupLine(
                $"[red]✗ Skill '{Markup.Escape(slug)}' not found.[/] " +
                $"Run [bold]fuseraft skills list[/] to see installed skills.");
            return 1;
        }

        Directory.Delete(destDir, recursive: true);

        await using var index = new SkillIndex();
        try
        {
            await index.RemoveAsync(slug, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("e_sqlite3") || ex.Message.Contains("SQLite"))
        {
            // Skill directory already deleted; index cleanup is best-effort.
            AnsiConsole.MarkupLine($"[yellow]⚠[/] Skill files removed but index update failed: {Markup.Escape(ex.Message)}");
        }

        AnsiConsole.MarkupLine($"[green]✓[/] Removed [bold]{Markup.Escape(slug)}[/].");
        return 0;
    }
}

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

// Shared helpers

file static class SkillsHelpers
{
    private static readonly Regex NameFrontmatter =
        new(@"^name:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex DescriptionFrontmatter =
        new(@"^description:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    internal static string? ExtractSlug(string content)
    {
        var m = NameFrontmatter.Match(content);
        if (!m.Success) return null;
        var name = m.Groups[1].Value.Trim().Trim('"').Trim('\'');
        return string.IsNullOrWhiteSpace(name) ? null : ToSlug(name);
    }

    internal static string ExtractDescription(string content)
    {
        var m = DescriptionFrontmatter.Match(content);
        if (!m.Success) return string.Empty;
        return m.Groups[1].Value.Trim().Trim('"').Trim('\'');
    }

    internal static string ToSlug(string name) =>
        Regex.Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');
}
