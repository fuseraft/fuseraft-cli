using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands;

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
            var expanded = ContextHelpers.ExpandSource(settings.Source);
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

// fuseraft context list

public sealed class ContextListSettings : CommandSettings
{
    [CommandOption("--dir")]
    [Description("Project directory containing .fuseraft/ (default: current directory).")]
    public string? Dir { get; set; }
}

public sealed class ContextListCommand : AsyncCommand<ContextListSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ContextListSettings settings, CancellationToken cancellationToken)
    {
        var contextDir = ContextHelpers.ResolveContextDir(settings.Dir);
        var store      = new ContextStore(contextDir);
        var index      = await store.LoadIndexAsync();

        if (index.Items.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[dim]No context items. Use [bold]fuseraft context add <path>[/] to import one.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Name[/]"))
            .AddColumn(new TableColumn("[bold]Files[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Imported[/]"))
            .AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var (_, item) in index.Items.OrderBy(x => x.Key))
        {
            var total = item.Files.Sum(f => f.SizeBytes);
            table.AddRow(
                Markup.Escape(item.Name),
                item.Files.Count.ToString(),
                ContextHelpers.FormatSize(total),
                item.ImportedAt.ToString("yyyy-MM-dd"),
                Markup.Escape(item.Description ?? string.Empty));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[dim]{index.Items.Count} item(s) stored in {Markup.Escape(contextDir)}[/]");
        return 0;
    }
}

// fuseraft context remove <name>

public sealed class ContextRemoveSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    [Description("Name of the context item to remove.")]
    public string Name { get; set; } = string.Empty;

    [CommandOption("--dir")]
    [Description("Project directory containing .fuseraft/ (default: current directory).")]
    public string? Dir { get; set; }
}

public sealed class ContextRemoveCommand : AsyncCommand<ContextRemoveSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ContextRemoveSettings settings, CancellationToken cancellationToken)
    {
        var contextDir = ContextHelpers.ResolveContextDir(settings.Dir);
        var store      = new ContextStore(contextDir);

        try
        {
            await store.RemoveAsync(settings.Name);
            AnsiConsole.MarkupLine($"[green]✓[/] Removed [bold]{Markup.Escape(settings.Name)}[/].");
        }
        catch (KeyNotFoundException)
        {
            AnsiConsole.MarkupLine(
                $"[red]✗ Context item '{Markup.Escape(settings.Name)}' not found.[/] " +
                $"Run [bold]fuseraft context list[/] to see available items.");
            return 1;
        }

        return 0;
    }
}

// Shared helpers (file-scoped so they don't pollute the assembly surface)

file static class ContextHelpers
{
    internal static string ResolveContextDir(string? dir)
    {
        var baseDir = string.IsNullOrWhiteSpace(dir)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(dir);
        return Path.Combine(baseDir, ContextStore.DefaultContextDir);
    }

    internal static string ExpandSource(string source)
    {
        if (source.StartsWith("~/") || source == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return source.Length > 2
                ? Path.Combine(home, source[2..])
                : home;
        }
        return Path.GetFullPath(source);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1_024         => $"{bytes} B",
        < 1_048_576     => $"{bytes / 1_024.0:F1} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        _               => $"{bytes / 1_073_741_824.0:F1} GB",
    };
}
