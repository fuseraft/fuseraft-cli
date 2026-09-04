using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Knowledge;

// fuseraft knowledge gc

public sealed class KnowledgeGcSettings : CommandSettings
{
    [CommandOption("--apply")]
    [Description("Commit all lifecycle changes to disk. Without this flag the command runs as a dry-run and prints what would change.")]
    public bool Apply { get; init; }

    [CommandOption("--lifecycle|-l <path>")]
    [Description("Path to lifecycle.yaml (default: .fuseraft/knowledge/lifecycle.yaml).")]
    public string? LifecyclePath { get; init; }

    [CommandOption("--graph <path>")]
    [Description("Override the repository graph path (default: .fuseraft/state/repository.graph).")]
    public string? GraphPath { get; init; }
}

public sealed class KnowledgeGcCommand : AsyncCommand<KnowledgeGcSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext     context,
        KnowledgeGcSettings settings,
        CancellationToken  cancellationToken)
    {
        var policy = KnowledgeLifecycleManager.LoadPolicy(settings.LifecyclePath);
        var slug   = FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory());

        var graphPath = settings.GraphPath
            ?? FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalRepositoryGraph, slug);

        var manager = new KnowledgeLifecycleManager(
            new AdrStore(FuseraftPaths.LocalDecisions),
            new RepositoryMemoryStore(FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalRepositoryMemory, slug)),
            new RepositoryGraphStore(graphPath),
            new ProvenanceRegistry(FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalProvenance, slug)));

        if (!settings.Apply)
        {
            AnsiConsole.MarkupLine("[bold yellow]Dry-run mode[/] — pass [bold]--apply[/] to commit changes.\n");
        }

        // Capture ephemeral state/log files before gc runs so we don't delete gc's own outputs.
        var ignoreRules   = FuseraftIgnoreRules.Load();
        var ephemeralPaths = settings.Apply && ignoreRules.HasRules
            ? CollectEphemeralStateFiles(slug, ignoreRules)
                .Concat(CollectEphemeralLogFiles(slug, ignoreRules))
                .ToList()
            : [];

        GcReport report;
        try
        {
            report = await AnsiConsole
                .Status()
                .StartAsync("Running knowledge lifecycle policies…", async _ =>
                    await manager.RunAsync(policy, settings.Apply, cancellationToken));
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]GC failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        PrintReport(report, settings.Apply);

        if (settings.Apply && ephemeralPaths.Count > 0)
        {
            var deleted = ephemeralPaths.Where(File.Exists).ToList();
            foreach (var f in deleted) File.Delete(f);
            if (deleted.Count > 0)
                AnsiConsole.MarkupLine(
                    $"[dim]Deleted {deleted.Count} ephemeral state/log file(s) per .fuseraftignore.[/]");
        }

        return 0;
    }

    /// <summary>
    /// Returns state files that exist on disk and are marked ephemeral by <paramref name="rules"/>.
    /// Excludes provenance.archive.json — gc writes to it; deleting it here would discard
    /// the records just compacted.
    /// </summary>
    private static List<string> CollectEphemeralStateFiles(string slug, FuseraftIgnoreRules rules)
    {
        var stateDir = FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalState, slug);
        if (!Directory.Exists(stateDir)) return [];

        return Directory.EnumerateFiles(stateDir)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                if (name.Equals("provenance.archive.json", StringComparison.OrdinalIgnoreCase))
                    return false;
                return rules.IsEphemeral("state/" + name);
            })
            .ToList();
    }

    /// <summary>
    /// Returns log files that exist on disk and are marked ephemeral by <paramref name="rules"/>.
    /// Scans the project's diagnostics directory (<see cref="FuseraftPaths.LocalLogs"/>) — not the
    /// per-session ctx-snapshot logs, which are pruned by <c>fuseraft sessions --cleanup</c> instead.
    /// Recurses so per-session files under logs/repl_events/ are matched too.
    /// </summary>
    private static List<string> CollectEphemeralLogFiles(string slug, FuseraftIgnoreRules rules)
    {
        var logDir = FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalLogs, slug);
        if (!Directory.Exists(logDir)) return [];

        return Directory.EnumerateFiles(logDir, "*", SearchOption.AllDirectories)
            .Where(f => rules.IsEphemeral("logs/" + Path.GetRelativePath(logDir, f)))
            .ToList();
    }

    private static void PrintReport(GcReport report, bool applied)
    {
        var verb = applied ? "archived" : "would archive";

        if (report.IsEmpty)
        {
            AnsiConsole.MarkupLine("[green]Nothing to do — all knowledge artifacts are within policy.[/]");
            return;
        }

        AnsiConsole.WriteLine();

        if (report.ArchivedDecisionIds.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold]Superseded ADRs[/] {verb} ({report.ArchivedDecisionIds.Count}):");
            foreach (var id in report.ArchivedDecisionIds)
                AnsiConsole.MarkupLine($"  [dim]→[/] {Markup.Escape(id)}  [dim](.fuseraft/knowledge/decisions/archive/)[/]");
            AnsiConsole.WriteLine();
        }

        if (report.DemotedMemoryIds.Count > 0)
        {
            var v2 = applied ? "demoted" : "would demote";
            AnsiConsole.MarkupLine($"[bold]Repository memories[/] {v2} Approved → Candidate ({report.DemotedMemoryIds.Count}):");
            foreach (var id in report.DemotedMemoryIds)
                AnsiConsole.MarkupLine($"  [dim]→[/] {Markup.Escape(id)}  [dim](not reinforced within window)[/]");
            AnsiConsole.WriteLine();
        }

        if (report.PrunedMemoryIds.Count > 0)
        {
            var v2 = applied ? "deleted" : "would delete";
            AnsiConsole.MarkupLine($"[bold]Stale candidate memories[/] {v2} ({report.PrunedMemoryIds.Count}):");
            foreach (var id in report.PrunedMemoryIds)
                AnsiConsole.MarkupLine($"  [dim]→[/] {Markup.Escape(id)}  [dim](Candidate, unreinforced past retention window)[/]");
            AnsiConsole.WriteLine();
        }

        if (report.DecayedClaimIds.Count > 0)
        {
            var v2 = applied ? "decayed" : "would decay";
            AnsiConsole.MarkupLine($"[bold]Provenance claims[/] {v2} Verified → Inferred ({report.DecayedClaimIds.Count}):");
            foreach (var id in report.DecayedClaimIds)
                AnsiConsole.MarkupLine($"  [dim]→[/] {Markup.Escape(id)}");
            AnsiConsole.WriteLine();
        }

        if (report.PrunedNodeIds.Count > 0)
        {
            var v2 = applied ? "pruned" : "would prune";
            AnsiConsole.MarkupLine($"[bold]Orphaned graph nodes[/] {v2} ({report.PrunedNodeIds.Count}):");
            foreach (var id in report.PrunedNodeIds)
                AnsiConsole.MarkupLine($"  [dim]→[/] {Markup.Escape(id)}");
            AnsiConsole.WriteLine();
        }

        if (report.ArchivedProvenanceIds.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold]Provenance records[/] {verb} ({report.ArchivedProvenanceIds.Count}):");
            AnsiConsole.MarkupLine($"  [dim]→ .fuseraft/state/provenance.archive.json[/]");
            AnsiConsole.WriteLine();
        }

        if (applied)
        {
            AnsiConsole.MarkupLine("[green]Knowledge GC complete.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Dry-run complete — no changes written.[/] Re-run with [bold]--apply[/] to commit.");
        }
    }
}
