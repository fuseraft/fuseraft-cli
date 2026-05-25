using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Context;

// fuseraft context remove <name>

public sealed class ContextRemoveSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    [Description("Name of the context item to remove.")]
    public string Name { get; set; } = string.Empty;

    [CommandOption("--dir")]
    [Description("Project directory containing .fuseraft/ (default: current directory).")]
    public string? Dir { get; set; }
}

public sealed class ContextRemoveCommand : AsyncCommand<ContextRemoveSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ContextRemoveSettings settings, CancellationToken cancellationToken)
    {
        var contextDir = ContextHelpers.ResolveContextDir(settings.Dir);
        var store      = new ContextStore(contextDir);

        try
        {
            await store.RemoveAsync(settings.Name);
            AnsiConsole.MarkupLine($"[green]✓[/] Removed [bold]{Markup.Escape(settings.Name)}[/].");
        }
        catch (KeyNotFoundException)
        {
            AnsiConsole.MarkupLine(
                $"[red]✗ Context item '{Markup.Escape(settings.Name)}' not found.[/] " +
                $"Run [bold]fuseraft context list[/] to see available items.");
            return 1;
        }

        return 0;
    }
}
