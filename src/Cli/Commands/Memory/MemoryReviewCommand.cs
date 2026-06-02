using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Memory;

// fuseraft memory review

public sealed class MemoryReviewSettings : CommandSettings
{
    [CommandOption("--dir <path>")]
    [Description("Repository memory directory (default: .fuseraft/knowledge/repository).")]
    public string? Directory { get; init; }

    [CommandOption("--all")]
    [Description("Show all entries including Approved and Rejected, not just Candidates.")]
    public bool All { get; init; }
}

public sealed class MemoryReviewCommand : AsyncCommand<MemoryReviewSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        MemoryReviewSettings settings,
        CancellationToken cancellationToken)
    {
        var dir   = settings.Directory ?? FuseraftPaths.LocalRepositoryMemory;
        var store = new RepositoryMemoryStore(dir);

        var entries = settings.All
            ? await store.LoadAllAsync(cancellationToken)
            : await store.LoadCandidatesAsync(cancellationToken);

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine(settings.All
                ? "[dim]No repository memory entries found.[/]"
                : "[dim]No candidate entries to review. Run a session first, or use [bold]--all[/] to view all entries.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]Repository Memory Review[/] — {entries.Count} entry/entries\n");

        int approved = 0, rejected = 0, skipped = 0;

        foreach (var entry in entries)
        {
            AnsiConsole.Write(new Rule());
            AnsiConsole.MarkupLine($"[bold]Pattern:[/] {Markup.Escape(entry.Pattern)}");
            AnsiConsole.MarkupLine($"[dim]Status:[/] {entry.Status}  [dim]Confidence:[/] {entry.Confidence}  [dim]Reinforced:[/] ×{entry.ReinforcementCount}");
            if (entry.Evidence.Count > 0)
                AnsiConsole.MarkupLine($"[dim]Evidence:[/] {string.Join(", ", entry.Evidence)}");
            AnsiConsole.WriteLine();

            if (!settings.All || entry.Status.Equals("Candidate", StringComparison.OrdinalIgnoreCase))
            {
                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Action?")
                        .AddChoices("Approve", "Reject", "Skip"));

                switch (choice)
                {
                    case "Approve":
                        await store.SaveAsync(entry with { Status = "Approved" }, cancellationToken);
                        AnsiConsole.MarkupLine("[green]✓ Approved[/]");
                        approved++;
                        break;
                    case "Reject":
                        await store.SaveAsync(entry with { Status = "Rejected" }, cancellationToken);
                        AnsiConsole.MarkupLine("[red]✗ Rejected[/]");
                        rejected++;
                        break;
                    default:
                        AnsiConsole.MarkupLine("[dim]Skipped[/]");
                        skipped++;
                        break;
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]({entry.Status} — no action needed)[/]");
            }

            AnsiConsole.WriteLine();
        }

        AnsiConsole.Write(new Rule());
        AnsiConsole.MarkupLine(
            $"Review complete: [green]{approved} approved[/]  [red]{rejected} rejected[/]  [dim]{skipped} skipped[/]");

        return 0;
    }
}
