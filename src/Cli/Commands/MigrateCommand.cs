using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Team;

namespace fuseraft.Cli.Commands;

/// <summary>Settings for <c>fuseraft migrate</c>.</summary>
public sealed class MigrateSettings : CommandSettings
{
    [CommandArgument(0, "<path>")]
    [Description("Config file in the old 'Orchestration:' format, or a team file whose agents use agentFile.")]
    public string Path { get; init; } = string.Empty;

    [CommandOption("-w|--write")]
    [Description("Overwrite the file in place instead of printing the converted config.")]
    public bool Write { get; init; }
}

/// <summary>Converts a pre-v2 <c>Orchestration:</c> config into a v2 team file, or a team's <c>agentFile</c> agents into markdown files.</summary>
public sealed class MigrateCommand : Command<MigrateSettings>
{
    public override int Execute(CommandContext context, MigrateSettings settings, CancellationToken cancellationToken)
    {
        if (File.Exists(settings.Path) && TryReadTeam(settings.Path) is { } team)
            return AgentFileMigrator.Applies(team)
                ? MigrateAgentFiles(settings)
                : Done($"{settings.Path} is already a current team file; nothing to migrate.");

        var innerComments = File.Exists(settings.Path) ? InnerComments(settings.Path) : 0;
        LegacyConfigConverter.Result result;
        try
        {
            var (config, legacy) = OrchestratorConfigLoader.LoadLegacyConfig(settings.Path);
            result = LegacyConfigConverter.Convert(config, legacy, LeadingComments(settings.Path));
            Team.TeamCompiler.Compile(ParseBack(result.Yaml));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (settings.Write)
        {
            File.WriteAllText(settings.Path, result.Yaml);
            AnsiConsole.MarkupLine($"[green]✓[/] Converted {Markup.Escape(settings.Path)}");
        }
        else
        {
            Console.Out.Write(result.Yaml);
        }

        if (innerComments > 0)
            Console.Error.WriteLine("note: comments inside the file weren't carried over; only the leading comment block was kept.");
        foreach (var note in result.Notes)
            Console.Error.WriteLine($"note: {note}");
        return 0;
    }

    private static int MigrateAgentFiles(MigrateSettings settings)
    {
        var innerComments = InnerComments(settings.Path);
        AgentFileMigrator.Result result;
        try
        {
            result = AgentFileMigrator.Convert(settings.Path, LeadingComments(settings.Path));
            CheckCompiles(settings.Path, result);
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (settings.Write)
        {
            foreach (var (path, content) in result.Files)
            {
                File.WriteAllText(path, content);
                AnsiConsole.MarkupLine($"[green]✓[/] Wrote {Markup.Escape(Path.GetRelativePath(".", path))}");
            }
            File.WriteAllText(settings.Path, result.TeamText);
            AnsiConsole.MarkupLine($"[green]✓[/] Converted {Markup.Escape(settings.Path)}");
        }
        else
        {
            Console.Out.Write(result.TeamText);
            foreach (var (path, _) in result.Files)
                Console.Error.WriteLine($"note: would write {Path.GetRelativePath(".", path)}; rerun with --write to write it.");
        }

        if (innerComments > 0)
            Console.Error.WriteLine("note: comments inside the file weren't carried over; only the leading comment block was kept.");
        foreach (var note in result.Notes)
            Console.Error.WriteLine($"note: {note}");
        return 0;
    }

    // A result that wouldn't load is never written.
    private static void CheckCompiles(string teamPath, AgentFileMigrator.Result result) =>
        TeamCompiler.Compile(TeamConfigLoader.Load(
            TeamConfigLoader.ParseObject(result.TeamText, teamPath), teamPath,
            result.Files.ToDictionary(f => f.Path, f => f.Content)));

    private static System.Text.Json.Nodes.JsonObject? TryReadTeam(string path)
    {
        try
        {
            var root = TeamConfigLoader.ReadObject(path);
            return TeamConfigLoader.Find(root, "Team") is not null ? root : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    private static int Done(string message)
    {
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
        return 0;
    }

    private static Core.Models.Team.TeamConfig ParseBack(string yaml)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fuseraft-migrate-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, yaml);
            return TeamConfigLoader.Load(path);
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    private static int InnerComments(string path)
    {
        if (!YamlConfigLoader.IsYamlPath(path)) return 0;
        return File.ReadLines(path)
            .SkipWhile(l => l.TrimStart().StartsWith('#') || l.Trim().Length == 0)
            .Count(l => l.TrimStart().StartsWith('#') || l.Contains(" # "));
    }

    private static string? LeadingComments(string path)
    {
        if (!YamlConfigLoader.IsYamlPath(path)) return null;
        var lines = File.ReadLines(path).TakeWhile(l => l.TrimStart().StartsWith('#') || l.Trim().Length == 0)
            .SkipWhile(l => l.Trim().Length == 0).ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }
}
