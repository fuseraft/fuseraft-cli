using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Context;

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
