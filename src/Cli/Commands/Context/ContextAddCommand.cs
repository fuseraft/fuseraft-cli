using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Context;

// fuseraft context add <source> [--name <alias>] [--description <desc>]

public sealed class ContextAddSettings : CommandSettings
{
    [CommandArgument(0, "<source>")]
    [Description("Path to the file or directory to import.")]
    public string Source { get; set; } = string.Empty;

    [CommandOption("-n|--name")]
    [Description("Short alias used to reference this item (default: source file/dir name without extension).")]
    public string? Name { get; set; }

    [CommandOption("-d|--description")]
    [Description("Human-readable description appended to the context block in agent prompts.")]
    public string? Description { get; set; }

    [CommandOption("--dir")]
    [Description("Project directory containing .fuseraft/ (default: current directory).")]
    public string? Dir { get; set; }
}

public sealed class ContextAddCommand : AsyncCommand<ContextAddSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ContextAddSettings settings, CancellationToken cancellationToken)
    {
        var contextDir = ContextHelpers.ResolveContextDir(settings.Dir);

        // Default alias: filename without extension for files, directory name for dirs.
        var name = settings.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            var expanded = FuseraftPaths.ExpandPath(settings.Source);
            name = File.Exists(expanded)
                ? Path.GetFileNameWithoutExtension(expanded)
                : Path.GetFileName(expanded.TrimEnd(Path.DirectorySeparatorChar,
                                                      Path.AltDirectorySeparatorChar));
            // Sanitise: replace anything outside [a-zA-Z0-9_-] with a hyphen.
            name = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '-').ToArray())
                      .Trim('-');
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine("[red]✗ Could not derive a name from the source path. Use --name to specify one.[/]");
            return 1;
        }

        var store = new ContextStore(contextDir);

        try
        {
            AnsiConsole.MarkupLine(
                $"[dim]Importing [bold]{Markup.Escape(settings.Source)}[/] " +
                $"as [bold]{Markup.Escape(name)}[/]…[/]");

            await store.AddAsync(settings.Source, name, settings.Description?.Trim());

            var index = await store.LoadIndexAsync();
            var item  = index.Items[name];
            var total = item.Files.Sum(f => f.SizeBytes);

            AnsiConsole.MarkupLine(
                $"[green]✓[/] [bold]{Markup.Escape(name)}[/] — " +
                $"{item.Files.Count} file(s), {ContextHelpers.FormatSize(total)}");

            if (item.Files.Count > 1)
                foreach (var f in item.Files.OrderBy(f => f.RelativePath))
                    AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(f.RelativePath)}[/]");

            if (item.ExtractionInfo is not null)
                foreach (var note in item.ExtractionInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(note)}[/]");

            AnsiConsole.MarkupLine(
                $"\n[dim]Agents will see this item listed in their system prompt " +
                $"and can read it via read_file from " +
                $".fuseraft/context/{Markup.Escape(name)}/[/]");
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        return 0;
    }
}
