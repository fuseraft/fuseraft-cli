using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Commands.Context;
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

    [CommandOption("--nuclear")]
    [Description("Extreme mode: also clears ALL global fuseraft state — logs, memories, session " +
                 "checkpoints/snapshots, orchestration run state, crash dumps, scratchpad — for every " +
                 "project, not just this one. Provider config, API keys, schedule definitions, and " +
                 "installed skills are never touched. Requires --apply to actually delete; prompts for " +
                 "an extra confirmation unless --yes is also passed.")]
    public bool Nuclear { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Skip the extra confirmation prompt required by --nuclear.")]
    public bool Yes { get; init; }
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

        return settings.Nuclear ? await RunNuclearAsync(settings) : 0;
    }

    private sealed record NuclearCategory(string Name, string Description, string[] Dirs, string[] Files);

    private static List<NuclearCategory> NuclearCategories() =>
    [
        new("logs",
            "REPL/provider-error/app logs and context snapshots, for every project",
            [FuseraftPaths.GlobalLogsRoot], []),
        new("memories",
            "Persistent REPL/agent memories and the per-project repository memory graph",
            [FuseraftPaths.GlobalMemoryRoot, FuseraftPaths.GlobalKnowledgeRoot], []),
        new("sessions",
            "Session checkpoints, REPL session snapshots, and postmortem snapshots, for every project",
            [FuseraftPaths.GlobalSessions, FuseraftPaths.GlobalReplSessions, FuseraftPaths.GlobalSnapshotsRoot], []),
        new("run state",
            "Orchestration run state — evidence graphs, change logs, provenance, repository graphs",
            [FuseraftPaths.GlobalStateRoot], []),
        new("crash dumps",
            "Crash dump JSON files",
            [FuseraftPaths.GlobalCrashDumps], []),
        new("scratchpad",
            "Global agent scratchpad files",
            [FuseraftPaths.GlobalScratchpad], []),
        new("skill curation log",
            "Skill auto-curation history",
            [], [FuseraftPaths.GlobalSkillCurationLog]),
    ];

    private static (int Files, long Bytes) NuclearStat(NuclearCategory c)
    {
        int files = 0; long bytes = 0;

        foreach (var dir in c.Dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                files++;
                try { bytes += new FileInfo(f).Length; } catch { /* file vanished mid-scan */ }
            }
        }

        foreach (var file in c.Files)
        {
            if (!File.Exists(file)) continue;
            files++;
            try { bytes += new FileInfo(file).Length; } catch { /* file vanished mid-scan */ }
        }

        return (files, bytes);
    }

    /// <summary>
    /// The extreme end of <c>--nuclear</c>: clears every reproducible, machine-generated file
    /// under the global <c>~/.fuseraft/</c> home across every project. Provider config, the key
    /// file, schedule definitions, and installed skills are never touched — those are settings
    /// and content, not history. A project's own <c>.fuseraft/</c> (the current working
    /// directory) is untouched too; that directory is user-authored and git-tracked.
    /// </summary>
    private static async Task<int> RunNuclearAsync(KnowledgeGcSettings settings)
    {
        var categories = NuclearCategories();
        var stats      = categories.ToDictionary(c => c.Name, NuclearStat);

        var totalFiles = stats.Values.Sum(s => s.Files);
        var totalBytes = stats.Values.Sum(s => s.Bytes);

        AnsiConsole.WriteLine();
        if (totalFiles == 0)
        {
            AnsiConsole.MarkupLine("[green]--nuclear: nothing to clear — the global fuseraft store is already empty.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Category[/]")
            .AddColumn("[bold]Files[/]").AddColumn("[bold]Size[/]")
            .AddColumn("[bold]Description[/]");

        foreach (var c in categories)
        {
            var (files, bytes) = stats[c.Name];
            if (files == 0) continue;
            table.AddRow(
                $"[bold]{Markup.Escape(c.Name)}[/]",
                files.ToString("N0"),
                ContextHelpers.FormatSize(bytes),
                $"[dim]{Markup.Escape(c.Description)}[/]");
        }

        AnsiConsole.MarkupLine("[bold red]--nuclear[/] — clears reproducible global state for [bold]every project[/]:");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{totalFiles:N0} file(s), {ContextHelpers.FormatSize(totalBytes)} total.[/]");
        AnsiConsole.MarkupLine(
            "[dim]Never touched: provider config, API keys, schedule definitions, installed skills, " +
            "and this project's own .fuseraft/ directory.[/]");

        if (!settings.Apply)
        {
            AnsiConsole.MarkupLine("[yellow]Nuclear dry-run — pass --apply to actually delete this.[/]");
            return 0;
        }

        if (!settings.Yes)
        {
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]✗ --nuclear --apply refused in a non-interactive session without --yes.[/]");
                return 1;
            }

            AnsiConsole.WriteLine();
            if (!AnsiConsole.Confirm(
                    "[bold red]Delete all of this now, for every project on this machine? This cannot be undone.[/]", false))
            {
                AnsiConsole.MarkupLine("[dim]Nuclear cleanup aborted. Nothing else was deleted.[/]");
                return 0;
            }
        }

        int deletedFiles = 0;
        long reclaimedBytes = 0;
        var errors = new List<string>();

        foreach (var c in categories)
        {
            foreach (var dir in c.Dirs)
            {
                if (!Directory.Exists(dir)) continue;
                var (files, bytes) = NuclearStat(new NuclearCategory(c.Name, c.Description, [dir], []));
                try   { Directory.Delete(dir, recursive: true); deletedFiles += files; reclaimedBytes += bytes; }
                catch (Exception ex) { errors.Add($"{dir}: {ex.Message}"); }
            }

            foreach (var file in c.Files)
            {
                if (!File.Exists(file)) continue;
                var size = new FileInfo(file).Length;
                try   { File.Delete(file); deletedFiles++; reclaimedBytes += size; }
                catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
            }
        }

        AnsiConsole.MarkupLine(
            $"[green]✓ Nuclear cleanup deleted {deletedFiles:N0} file(s) ({ContextHelpers.FormatSize(reclaimedBytes)} reclaimed).[/]");

        if (errors.Count == 0) return 0;

        AnsiConsole.MarkupLine($"[yellow]{errors.Count} path(s) could not be deleted:[/]");
        foreach (var e in errors) AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(e)}[/]");
        return 1;
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
