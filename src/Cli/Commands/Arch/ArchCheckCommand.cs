using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Arch;

// fuseraft arch check

public sealed class ArchCheckSettings : CommandSettings
{
    [CommandOption("--manifest|-m <path>")]
    [Description("Path to the architecture manifest. Defaults to .fuseraft/architecture.yaml.")]
    public string? ManifestPath { get; init; }

    [CommandOption("--dir|-d <dir>")]
    [Description("Root directory to scan. Defaults to the current working directory.")]
    public string? Directory { get; init; }
}

public sealed class ArchCheckCommand : AsyncCommand<ArchCheckSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ArchCheckSettings settings,
        CancellationToken cancellationToken)
    {
        var manifestPath = settings.ManifestPath ?? FuseraftPaths.LocalArchitectureManifest;
        var projectRoot  = settings.Directory is not null
            ? Path.GetFullPath(settings.Directory)
            : System.IO.Directory.GetCurrentDirectory();

        var manifest = ArchitectureScanner.TryLoadManifest(manifestPath);
        if (manifest is null)
        {
            AnsiConsole.MarkupLine($"[yellow]No manifest found at[/] [dim]{Markup.Escape(manifestPath)}[/]");
            AnsiConsole.MarkupLine("[grey]Create .fuseraft/architecture.yaml to enable drift detection.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]Architecture check[/]  manifest: [dim]{Markup.Escape(manifestPath)}[/]");
        AnsiConsole.MarkupLine($"  Root: [dim]{Markup.Escape(projectRoot)}[/]");
        AnsiConsole.WriteLine();

        var violations = await ArchitectureScanner.ScanAsync(manifest, projectRoot, cancellationToken);

        if (violations.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No violations found.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[red bold]{violations.Count} violation(s) found:[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]File[/]")
            .AddColumn("[bold]Line[/]")
            .AddColumn("[bold]Source Layer[/]")
            .AddColumn("[bold]Target Layer[/]")
            .AddColumn("[bold]Namespace[/]");

        foreach (var v in violations)
        {
            table.AddRow(
                Markup.Escape(v.File),
                v.Line.ToString(),
                $"[yellow]{Markup.Escape(v.SourceLayer)}[/]",
                $"[red]{Markup.Escape(v.TargetLayer)}[/]",
                Markup.Escape(v.Namespace));
        }

        AnsiConsole.Write(table);
        return 1;
    }
}
