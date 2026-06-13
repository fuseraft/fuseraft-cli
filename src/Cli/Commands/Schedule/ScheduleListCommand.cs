using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;

namespace fuseraft.Cli.Commands.Schedule;

// fuseraft schedule list

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
