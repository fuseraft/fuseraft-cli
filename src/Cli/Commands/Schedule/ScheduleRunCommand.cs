using System.ComponentModel;
using System.Diagnostics;
using Cronos;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Core.Models;

namespace fuseraft.Cli.Commands.Schedule;

// fuseraft schedule run

public sealed class ScheduleRunSettings : CommandSettings
{
    [CommandOption("-n|--name")]
    [Description("Force-run a specific job by name, ignoring its schedule. Omit to tick all due jobs.")]
    public string? Name { get; set; }

    [CommandOption("--dry-run")]
    [Description("Show which jobs would execute without running them.")]
    public bool DryRun { get; set; }
}

public sealed class ScheduleRunCommand : AsyncCommand<ScheduleRunSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ScheduleRunSettings settings,
        CancellationToken cancellationToken)
    {
        var dir = FuseraftPaths.GlobalSchedule;
        if (!Directory.Exists(dir))
        {
            AnsiConsole.MarkupLine("[dim]No scheduled jobs found.[/]");
            return 0;
        }

        IEnumerable<string> files = settings.Name is { Length: > 0 } name
            ? [Path.Combine(dir, $"{ScheduleUtil.ToSlug(name)}.yaml")]
            : Directory.GetFiles(dir, "*.yaml");

        var ran     = 0;
        var skipped = 0;

        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                AnsiConsole.MarkupLine($"[red]✗ Job not found:[/] {Markup.Escape(Path.GetFileNameWithoutExtension(file))}");
                return 1;
            }

            ScheduledJob job;
            try { job = ScheduleUtil.Deserialize(await File.ReadAllTextAsync(file, cancellationToken))!; }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Cannot parse {Markup.Escape(file)}:[/] {Markup.Escape(ex.Message)}");
                continue;
            }

            var forced = settings.Name is { Length: > 0 };
            var isDue  = job.NextRun is null || job.NextRun <= DateTimeOffset.UtcNow;

            if (!job.Enabled && !forced)
            {
                AnsiConsole.MarkupLine($"[dim]Skipped (disabled):[/] {Markup.Escape(job.Name)}");
                skipped++;
                continue;
            }

            if (!isDue && !forced)
            {
                AnsiConsole.MarkupLine(
                    $"[dim]Skipped (next run {job.NextRun:yyyy-MM-dd HH:mm} UTC):[/] {Markup.Escape(job.Name)}");
                skipped++;
                continue;
            }

            var lockPath = Path.ChangeExtension(file, ".lock");
            if (File.Exists(lockPath))
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ Skipped (lock file present — may already be running):[/] {Markup.Escape(job.Name)}");
                skipped++;
                continue;
            }

            if (settings.DryRun)
            {
                AnsiConsole.MarkupLine($"[dim]Would run:[/] [bold]{Markup.Escape(job.Name)}[/]  [dim]{Markup.Escape(job.Cron)}[/]");
                ran++;
                continue;
            }

            AnsiConsole.MarkupLine($"[dim]→ Running:[/] [bold]{Markup.Escape(job.Name)}[/]  [dim]{Markup.Escape(job.Cron)}[/]");
            var exitCode = await ExecuteJobAsync(job, lockPath, file, cancellationToken);

            AnsiConsole.MarkupLine(exitCode == 0
                ? $"[green]✓ Completed:[/] [bold]{Markup.Escape(job.Name)}[/]"
                : $"[red]✗ Failed (exit {exitCode}):[/] [bold]{Markup.Escape(job.Name)}[/]");
            ran++;
        }

        if (settings.DryRun)
            AnsiConsole.MarkupLine($"\n[dim]Dry run: {ran} job(s) would execute, {skipped} skipped.[/]");
        else if (ran == 0 && skipped > 0)
            AnsiConsole.MarkupLine("[dim]No jobs were due. Use --dry-run to preview.[/]");

        return 0;
    }

    private static async Task<int> ExecuteJobAsync(
        ScheduledJob job,
        string lockPath,
        string jobFilePath,
        CancellationToken ct)
    {
        // Acquire lock
        try { await File.WriteAllTextAsync(lockPath, DateTimeOffset.UtcNow.ToString("O"), ct); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Could not write lock file for {Markup.Escape(job.Name)} — concurrent runs are not protected:[/] {Markup.Escape(ex.Message)}");
        }

        var exitCode = 0;
        try
        {
            var exePath = Environment.ProcessPath ?? "fuseraft";
            var now     = DateTimeOffset.UtcNow;
            var args    = BuildArgs(job);
            var psi     = new ProcessStartInfo(exePath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            var resolvedOutput = ResolveOutputPath(job, now);
            if (resolvedOutput is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resolvedOutput)!);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError  = true;
            }

            using var process = Process.Start(psi)!;

            if (resolvedOutput is not null)
            {
                await using var writer = new StreamWriter(resolvedOutput, append: false);
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                await writer.WriteAsync(await stdoutTask);
                await writer.WriteAsync(await stderrTask);
            }
            else
            {
                await process.WaitForExitAsync(ct);
            }

            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]  Execution error:[/] {Markup.Escape(ex.Message)}");
            exitCode = 1;
        }
        finally
        {
            try { if (File.Exists(lockPath)) File.Delete(lockPath); }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ Could not delete lock file {Markup.Escape(lockPath)} — remove it manually or the next run of {Markup.Escape(job.Name)} will be skipped:[/] {Markup.Escape(ex.Message)}");
            }
        }

        // Update job state regardless of exit code
        try
        {
            var text     = await File.ReadAllTextAsync(jobFilePath, ct);
            var reloaded = ScheduleUtil.Deserialize(text);
            if (reloaded is not null)
            {
                CronExpression? cronExpr = null;
                try { cronExpr = CronExpression.Parse(reloaded.Cron); }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠ Could not parse cron expression '{Markup.Escape(reloaded.Cron)}' for {Markup.Escape(job.Name)} — NextRun will not be set:[/] {Markup.Escape(ex.Message)}");
                }

                reloaded.LastRun = DateTimeOffset.UtcNow;
                reloaded.NextRun = cronExpr?.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
                await File.WriteAllTextAsync(jobFilePath, ScheduleUtil.Serialize(reloaded), ct);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Could not persist state for {Markup.Escape(job.Name)} — LastRun and NextRun were not updated:[/] {Markup.Escape(ex.Message)}");
        }

        return exitCode;
    }

    private static List<string> BuildArgs(ScheduledJob job)
    {
        var args = new List<string> { job.Task, "--no-banner" };
        if (job.Config  is { Length: > 0 } cfg) { args.Add("--config");   args.Add(cfg); }
        if (job.WorkDir is { Length: > 0 } wd)  { args.Add("--work-dir"); args.Add(wd); }
        return args;
    }

    private static string? ResolveOutputPath(ScheduledJob job, DateTimeOffset now) =>
        job.OutputPath is { Length: > 0 } template
            ? FuseraftPaths.ExpandPath(template
                .Replace("{name}", job.Name)
                .Replace("{date}", now.ToString("yyyy-MM-dd"))
                .Replace("{time}", now.ToString("HHmm")))
            : null;
}
