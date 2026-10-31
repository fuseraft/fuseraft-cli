using System.ComponentModel;
using Cronos;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;

namespace fuseraft.Cli.Commands.Schedule;

// fuseraft schedule enable <name>
// fuseraft schedule disable <name>

public sealed class ScheduleToggleSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    [Description("Name of the job.")]
    public string Name { get; set; } = string.Empty;
}

public abstract class ScheduleToggleCommand(bool enabled) : AsyncCommand<ScheduleToggleSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ScheduleToggleSettings settings, CancellationToken cancellationToken)
    {
        var slug    = ScheduleUtil.ToSlug(settings.Name);
        var jobPath = Path.Combine(FuseraftPaths.GlobalSchedule, $"{slug}.yaml");

        if (!File.Exists(jobPath))
        {
            AnsiConsole.MarkupLine($"[red]✗ Job not found:[/] {Markup.Escape(slug)}");
            return 1;
        }

        var job = ScheduleUtil.Deserialize(await File.ReadAllTextAsync(jobPath, cancellationToken));
        if (job is null)
        {
            AnsiConsole.MarkupLine($"[red]✗ Job file is empty or corrupt:[/] {Markup.Escape(jobPath)}");
            return 1;
        }

        // Re-enabling resumes from now: a run missed while disabled shouldn't fire on the next tick.
        if (enabled && !job.Enabled)
        {
            try { job.NextRun = CronExpression.Parse(job.Cron).GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc); }
            catch (CronFormatException) { }
        }

        job.Enabled = enabled;
        await File.WriteAllTextAsync(jobPath, ScheduleUtil.Serialize(job), cancellationToken);

        AnsiConsole.MarkupLine($"[green]✓ {(enabled ? "Enabled" : "Disabled")}:[/] [bold]{Markup.Escape(slug)}[/]");
        return 0;
    }
}

public sealed class ScheduleEnableCommand() : ScheduleToggleCommand(enabled: true);

public sealed class ScheduleDisableCommand() : ScheduleToggleCommand(enabled: false);
