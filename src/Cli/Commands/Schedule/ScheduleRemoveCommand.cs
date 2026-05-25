using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;

namespace fuseraft.Cli.Commands.Schedule;

// fuseraft schedule remove

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
