using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Graph;

// fuseraft graph build

public sealed class GraphBuildSettings : CommandSettings
{
    [CommandOption("--dir|-d <dir>")]
    [Description("Root directory to scan. Defaults to the current working directory.")]
    public string? Directory { get; init; }

    [CommandOption("--output|-o <path>")]
    [Description("Output path for the graph file. Defaults to .fuseraft/state/repository.graph.")]
    public string? OutputPath { get; init; }
}

public sealed class GraphBuildCommand : AsyncCommand<GraphBuildSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        GraphBuildSettings settings,
        CancellationToken cancellationToken)
    {
        var root       = settings.Directory is not null
            ? Path.GetFullPath(settings.Directory)
            : Directory.GetCurrentDirectory();

        var outputPath = settings.OutputPath
            ?? FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalRepositoryGraph, FuseraftPaths.ProjectSlug(root));
        var store      = new RepositoryGraphStore(outputPath);
        var builder    = new RepositoryGraphBuilder(store, root);

        AnsiConsole.MarkupLine($"[bold]Building repository graph[/] from [dim]{Markup.Escape(root)}[/]");
        AnsiConsole.MarkupLine($"  Output: [dim]{Markup.Escape(outputPath)}[/]");
        AnsiConsole.WriteLine();

        (int nodes, int edges) = (0, 0);
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Scanning source files…", async ctx =>
            {
                (nodes, edges) = await builder.BuildAllAsync(root, cancellationToken);
                ctx.Status($"Saving graph ({nodes:N0} nodes, {edges:N0} edges)…");
            });

        AnsiConsole.MarkupLine($"[green]Done.[/] {nodes:N0} nodes · {edges:N0} edges written to [dim]{Markup.Escape(outputPath)}[/]");
        return 0;
    }
}
