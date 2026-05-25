using System.ComponentModel;
using Cronos;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Core.Models;

namespace fuseraft.Cli.Commands.Schedule;

// fuseraft schedule add

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
