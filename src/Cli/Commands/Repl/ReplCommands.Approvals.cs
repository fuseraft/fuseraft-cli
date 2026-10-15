using Spectre.Console;

namespace fuseraft.Cli.Commands.Repl;

// /approvals: the "always" answers given to HITL approvals, and forgetting them.
internal static partial class ReplCommands
{
    private static CommandResult CmdApprovals(ReplSessionContext ctx, string arg)
    {
        if (ctx.Approvals is not { } approvals)
        {
            AnsiConsole.MarkupLine("[dim]This session keeps no \"always\" answers.[/]");
            return CommandResult.Continue;
        }
        switch (arg)
        {
            case "":
                var (programs, tools, session) = approvals.Describe();
                if (programs.Count + tools.Count + session.Count == 0)
                    AnsiConsole.MarkupLine("[dim]No \"always\" answers yet. Answer a HITL approval with[/] a [dim]to stop being asked for that kind of action.[/]");
                if (programs.Count > 0)
                    AnsiConsole.MarkupLine($"Commands using: {Markup.Escape(string.Join(", ", programs))}");
                if (tools.Count > 0)
                    AnsiConsole.MarkupLine($"Tools: {Markup.Escape(string.Join(", ", tools))}");
                if (programs.Count + tools.Count > 0)
                    AnsiConsole.MarkupLine($"[dim]Saved for this directory in {Markup.Escape(approvals.Store?.FilePath ?? "")}.[/]");
                if (session.Count > 0)
                {
                    AnsiConsole.MarkupLine("[dim]For this session only:[/]");
                    foreach (var item in session)
                        AnsiConsole.MarkupLine($"  {Markup.Escape(item)}");
                }
                break;
            case "clear":
                approvals.Clear();
                AnsiConsole.MarkupLine("[dim]Forgot every \"always\" answer, saved and from this session; approvals will ask again.[/]");
                break;
            default:
                AnsiConsole.MarkupLine("[yellow]Usage:[/] /approvals, or /approvals clear");
                break;
        }
        return CommandResult.Continue;
    }
}
