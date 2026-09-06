using Spectre.Console;

namespace fuseraft.Cli.Commands.Repl;

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /undo
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdUndoAsync(ReplSessionContext ctx)
    {
        if (ctx.UndoStore is null)
        {
            AnsiConsole.MarkupLine("[dim]File tools are disabled this session (--no-tools) — nothing to undo.[/]");
            return CommandResult.Continue;
        }

        var result = await ctx.UndoStore.UndoLastTurnAsync();
        if (result is null)
        {
            AnsiConsole.MarkupLine("[dim]Nothing to undo.[/]");
            return CommandResult.Continue;
        }

        AnsiConsole.MarkupLine(
            $"[green]Restored {result.Actions.Count} file(s) from turn {result.TurnRestored}:[/]");
        foreach (var action in result.Actions)
            AnsiConsole.MarkupLine($"  [dim]·[/] {Markup.Escape(action.Path)} [dim]({Markup.Escape(action.Description)})[/]");

        return CommandResult.Continue;
    }
}
