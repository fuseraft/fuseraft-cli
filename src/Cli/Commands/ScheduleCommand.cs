using System.ComponentModel;
using System.Diagnostics;
using Cronos;
using Spectre.Console;
using Spectre.Console.Cli;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using fuseraft.Core;
using fuseraft.Core.Models;

namespace fuseraft.Cli.Commands;

// schedule add

public sealed class ScheduleAddSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    [Description("Unique job name used as the filename slug (e.g. 'nightly-audit').")]
    public string Name { get; set; } = string.Empty;

    [CommandOption("--cron")]
    [Description("5-field cron expression (e.g. '0 2 * * *' for 2 AM UTC daily).")]
    public string Cron { get; set; } = string.Empty;

    [CommandOption("-t|--task")]
    [Description("Task description passed to 'fuseraft run' as the session goal.")]
    public string Task { get; set; } = string.Empty;

    [CommandOption("-c|--config")]
    [Description("Path to the orchestration config YAML. Defaults to config/orchestration.yaml.")]
    public string? Config { get; set; }

    [CommandOption("--work-dir")]
    [Description("Working directory for the session.")]
    public string? WorkDir { get; set; }

    [CommandOption("-o|--output")]
    [Description("Output transcript path template. Supports {name}, {date}, {time} substitutions.")]
    public string? OutputPath { get; set; }

    [CommandOption("-d|--description")]
    [Description("Human-readable description shown in 'fuseraft schedule list'.")]
    public string? Description { get; set; }
}

public sealed class ScheduleAddCommand : AsyncCommand<ScheduleAddSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ScheduleAddSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Name))
        { AnsiConsole.MarkupLine("[red]✗ Name is required.[/]"); return 1; }
        if (string.IsNullOrWhiteSpace(settings.Cron))
        { AnsiConsole.MarkupLine("[red]✗ --cron is required.[/]"); return 1; }
        if (string.IsNullOrWhiteSpace(settings.Task))
        { AnsiConsole.MarkupLine("[red]✗ --task is required.[/]"); return 1; }

        CronExpression cronExpr;
        try { cronExpr = CronExpression.Parse(settings.Cron); }
        catch (CronFormatException ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Invalid cron expression:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var slug    = ScheduleUtil.ToSlug(settings.Name);
        var dir     = FuseraftPaths.GlobalSchedule;
        var jobPath = Path.Combine(dir, $"{slug}.yaml");

        if (File.Exists(jobPath))
        {
            AnsiConsole.MarkupLine($"[red]✗ Job '{Markup.Escape(slug)}' already exists.[/]");
            AnsiConsole.MarkupLine("[dim]Use 'fuseraft schedule remove' first if you want to replace it.[/]");
            return 1;
        }

        var nextRun = cronExpr.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var job = new ScheduledJob
        {
            Name        = slug,
            Description = settings.Description,
            Cron        = settings.Cron,
            Task        = settings.Task,
            Config      = settings.Config,
            WorkDir     = settings.WorkDir,
            OutputPath  = settings.OutputPath,
            Enabled     = true,
            CreatedAt   = DateTimeOffset.UtcNow,
            NextRun     = nextRun,
        };

        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(jobPath, ScheduleUtil.Serialize(job), cancellationToken);

        AnsiConsole.MarkupLine($"[green]✓ Scheduled:[/] [bold]{Markup.Escape(slug)}[/]");
        if (nextRun is not null)
            AnsiConsole.MarkupLine($"[dim]Next run: {nextRun:yyyy-MM-dd HH:mm} UTC[/]");
        AnsiConsole.MarkupLine($"[dim]Saved: {Markup.Escape(jobPath)}[/]");
        AnsiConsole.MarkupLine("[dim]To execute due jobs, run: fuseraft schedule run[/]");
        return 0;
    }
}

// schedule list

public sealed class ScheduleListSettings : CommandSettings { }

public sealed class ScheduleListCommand : AsyncCommand<ScheduleListSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, ScheduleListSettings settings, CancellationToken cancellationToken)
    {
        var dir = FuseraftPaths.GlobalSchedule;
        if (!Directory.Exists(dir) || Directory.GetFiles(dir, "*.yaml").Length == 0)
        {
            AnsiConsole.MarkupLine("[dim]No scheduled jobs found. Use 'fuseraft schedule add' to create one.[/]");
            return Task.FromResult(0);
        }

        var jobs = new List<ScheduledJob>();
        foreach (var file in Directory.GetFiles(dir, "*.yaml"))
        {
            try
            {
                var job = ScheduleUtil.Deserialize(File.ReadAllText(file));
                if (job is not null) jobs.Add(job);
            }
            catch { /* skip malformed files */ }
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Name")
            .AddColumn("Cron")
            .AddColumn("Next Run (UTC)")
            .AddColumn("Last Run (UTC)")
            .AddColumn("Enabled");

        foreach (var job in jobs.OrderBy(j => j.NextRun ?? DateTimeOffset.MaxValue))
        {
            var isDue    = job.Enabled && job.NextRun <= DateTimeOffset.UtcNow;
            var nameCell = isDue
                ? $"[yellow]{Markup.Escape(job.Name)}[/] [dim yellow](due)[/]"
                : Markup.Escape(job.Name);

            table.AddRow(
                nameCell,
                Markup.Escape(job.Cron),
                job.NextRun.HasValue ? job.NextRun.Value.ToString("yyyy-MM-dd HH:mm") : "[dim]—[/]",
                job.LastRun.HasValue ? job.LastRun.Value.ToString("yyyy-MM-dd HH:mm") : "[dim]never[/]",
                job.Enabled ? "[green]yes[/]" : "[dim]no[/]");
        }

        AnsiConsole.Write(table);
        return Task.FromResult(0);
    }
}

// schedule remove

public sealed class ScheduleRemoveSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    [Description("Name of the job to remove.")]
    public string Name { get; set; } = string.Empty;
}

public sealed class ScheduleRemoveCommand : AsyncCommand<ScheduleRemoveSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, ScheduleRemoveSettings settings, CancellationToken cancellationToken)
    {
        var slug    = ScheduleUtil.ToSlug(settings.Name);
        var jobPath = Path.Combine(FuseraftPaths.GlobalSchedule, $"{slug}.yaml");

        if (!File.Exists(jobPath))
        {
            AnsiConsole.MarkupLine($"[red]✗ Job not found:[/] {Markup.Escape(slug)}");
            return Task.FromResult(1);
        }

        File.Delete(jobPath);

        var lockPath = Path.ChangeExtension(jobPath, ".lock");
        if (File.Exists(lockPath)) File.Delete(lockPath);

        AnsiConsole.MarkupLine($"[green]✓ Removed:[/] [bold]{Markup.Escape(slug)}[/]");
        return Task.FromResult(0);
    }
}

// schedule run

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
        catch { /* lock write failure is non-fatal */ }

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
            catch { /* ignore */ }
        }

        // Update job state regardless of exit code
        try
        {
            var text     = await File.ReadAllTextAsync(jobFilePath, ct);
            var reloaded = ScheduleUtil.Deserialize(text);
            if (reloaded is not null)
            {
                CronExpression? cronExpr = null;
                try { cronExpr = CronExpression.Parse(reloaded.Cron); } catch { }

                reloaded.LastRun = DateTimeOffset.UtcNow;
                reloaded.NextRun = cronExpr?.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
                await File.WriteAllTextAsync(jobFilePath, ScheduleUtil.Serialize(reloaded), ct);
            }
        }
        catch { /* state update failure is non-fatal */ }

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

// Shared helpers (file-scoped)

file static class ScheduleUtil
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static string Serialize(ScheduledJob job)     => YamlSerializer.Serialize(job);
    public static ScheduledJob? Deserialize(string yaml) => YamlDeserializer.Deserialize<ScheduledJob>(yaml);
    public static string ToSlug(string name)             =>
        System.Text.RegularExpressions.Regex
            .Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
}
