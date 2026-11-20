using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure.Storage;

namespace fuseraft.Cli.Commands.Log;

// fuseraft log cache

public sealed class LogCacheSettings : CommandSettings
{
    [CommandOption("--session")]
    [Description("The session to report on (ID or prefix). Defaults to the latest session in this project.")]
    public string? Session { get; set; }

    [CommandOption("--all")]
    [Description("Report on every session in this project, one line each, then all of them together.")]
    public bool All { get; set; }

    [CommandOption("--path")]
    [Description("Read this REPL event log file instead.")]
    public string? Path { get; set; }
}

/// <summary>Summarizes how much of each REPL request the provider's prompt cache served, and why the rest missed.</summary>
public sealed class LogCacheCommand : AsyncCommand<LogCacheSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context, LogCacheSettings settings, CancellationToken cancellationToken)
    {
        var slug = FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory());
        var dir  = FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalReplEventsDir, slug);
        var logs = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.jsonl").OrderBy(File.GetLastWriteTimeUtc).ToList()
            : [];

        List<string> paths;
        if (!string.IsNullOrWhiteSpace(settings.Path))
            paths = [FuseraftPaths.ExpandPath(settings.Path)];
        else if (!string.IsNullOrWhiteSpace(settings.Session))
            paths = logs.Where(f => System.IO.Path.GetFileNameWithoutExtension(f)
                .StartsWith(settings.Session.Trim(), StringComparison.OrdinalIgnoreCase)).TakeLast(1).ToList();
        else
            paths = settings.All ? logs : logs.TakeLast(1).ToList();

        if (paths.Count == 0 || !paths.All(File.Exists))
        {
            AnsiConsole.MarkupLine(settings.Session is { Length: > 0 } s
                ? $"[yellow]No REPL session log matches '{Markup.Escape(s)}' in this project.[/]"
                : "[yellow]No REPL session logs for this project.[/] Run it from the directory you started fuseraft repl in.");
            return 1;
        }

        var reports = new List<CacheReport>();
        foreach (var path in paths)
            reports.Add(CacheReport.Analyze(await ReadEventsAsync(path, cancellationToken)));

        var config = UserConfigStore.Load().Config;
        var compactAt = ReplLimits.From(config?.Repl).AutoCompactThreshold;
        (int, string)? Budget(CacheReport report) =>
            report.Model is null || report.Model.EndsWith(" models", StringComparison.Ordinal) ? null
            : (ModelContextWindow.GetBudget(report.Model, config?.Repl?.ContextBudget),
               config?.Repl?.ContextBudget is > 0 ? "repl.contextBudget" : "estimated from the model name");

        if (reports.Count == 1)
        {
            Console.WriteLine(reports[0].Render(Budget(reports[0]), compactAt));
            return 0;
        }

        foreach (var r in reports)
            Console.WriteLine(
                $"{r.Session,-10} {r.Model,-24} {r.Requests + r.SubagentRequests,6:N0} requests  " +
                (r.PromptTokens > 0 ? $"{(double)r.CachedTokens / r.PromptTokens,4:P0} cached  " : "  no cache data  ") +
                $"{r.MissCount,4:N0} misses  {r.Compactions,3:N0} compactions");
        Console.WriteLine();
        var total = CacheReport.Combine(reports);
        Console.WriteLine(total.Render(Budget(total), compactAt));
        return 0;
    }

    private static async Task<List<JsonElement>> ReadEventsAsync(string path, CancellationToken ct)
    {
        var events = new List<JsonElement>();
        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                events.Add(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                // A line cut short by a crash: skip it.
            }
        }
        return events;
    }
}
