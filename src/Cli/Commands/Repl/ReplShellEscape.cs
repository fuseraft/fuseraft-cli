using System.Diagnostics;
using System.Text.RegularExpressions;
using Spectre.Console;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Handles the REPL's <c>!&lt;command&gt;</c> shell escape — a way to run a real shell
/// command without leaving the REPL for another terminal. Deliberately bypasses every
/// LLM-safety gate <see cref="ShellPlugin"/> enforces (sudo denial, <c>ShellPolicy</c>,
/// HITL approval, sandbox confinement, output truncation, timeout): those exist to guard
/// commands the *model* chose to run unsupervised, not one the user just typed themselves.
///
/// <para>
/// The child process inherits the console's stdio directly (no redirection), so interactive
/// programs (less, vim, ssh, an interactive installer prompt) work exactly as they would in
/// a real terminal, and long-running output streams live instead of buffering until exit.
/// The command and its output are never added to conversation history — this is meant to
/// feel like a second terminal, not a tool call the model can see.
/// </para>
/// </summary>
internal static class ReplShellEscape
{
    internal static bool IsShellEscape(string raw) => raw.Length > 0 && raw[0] == '!';

    private static readonly Regex CdPattern =
        new(@"^cd(?:\s+(.*))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static async Task RunAsync(ReplSessionContext ctx, string raw, CancellationToken cancellationToken)
    {
        var commandText = raw.Length > 1 ? raw[1..].Trim() : string.Empty;

        // "!!" repeats the last shell command: after stripping the leading '!' above, the
        // remaining text is itself just "!".
        if (commandText == "!")
        {
            if (ctx.LastShellCommand is null)
            {
                AnsiConsole.MarkupLine("[yellow]No previous shell command to repeat.[/]");
                return;
            }
            commandText = ctx.LastShellCommand;
        }

        if (commandText.Length == 0)
        {
            AnsiConsole.MarkupLine(
                "[dim]Usage: [/][bold]!<command>[/][dim]  (e.g. !git status)  ·  [/][bold]!![/][dim] repeats the last one[/]");
            return;
        }

        ctx.LastShellCommand = commandText;

        if (TryHandleCd(ctx, commandText))
            return;

        if (ctx.JsonMode)
        {
            // The child would inherit this process's stdio directly (see below), which in
            // the VS Code webview bridge is a JSONL pipe, not a real terminal — piping raw
            // shell output into it would corrupt the protocol. Not supported there yet.
            ReplJsonBridge.Emit(new
            {
                type = "error",
                text = "Shell escape (!<command>) isn't supported in VS Code webview mode yet — use the integrated terminal.",
            });
            return;
        }

        await RunInheritedAsync(ctx, commandText, cancellationToken);
    }

    // "cd" is handled here rather than spawned as a child process: a subprocess's own
    // directory change never outlives that subprocess, so without this special case every
    // `!cd ..` would silently do nothing and the next `!` command would still run in the
    // old directory — surprising, since a real shell's cd obviously persists.
    private static bool TryHandleCd(ReplSessionContext ctx, string commandText)
    {
        var match = CdPattern.Match(commandText);
        if (!match.Success) return false;

        var arg = match.Groups[1].Value.Trim();
        string target;
        if (arg.Length == 0)
        {
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (arg == "-")
        {
            if (ctx.PrevShellCwd is null)
            {
                AnsiConsole.MarkupLine("[yellow]cd: no previous directory.[/]");
                return true;
            }
            target = ctx.PrevShellCwd;
        }
        else
        {
            target = ProcessHelper.ExpandHome(arg);
            if (!Path.IsPathRooted(target))
                target = Path.Combine(ctx.ShellCwd, target);
        }

        target = Path.GetFullPath(target);

        if (!Directory.Exists(target))
        {
            AnsiConsole.MarkupLine($"[red]cd: no such directory:[/] {Markup.Escape(target)}");
            return true;
        }

        ctx.PrevShellCwd = ctx.ShellCwd;
        ctx.ShellCwd     = target;
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(target)}[/]");
        return true;
    }

    private static async Task RunInheritedAsync(ReplSessionContext ctx, string commandText, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName         = ShellEnvironment.Shell,
            WorkingDirectory = ctx.ShellCwd,
            UseShellExecute  = false,
            // Deliberately NOT redirected: the child inherits this console's stdin/stdout/
            // stderr directly, so it behaves like a normal foreground command in a real
            // terminal — interactive prompts, pagers, and live-streaming output all work,
            // and a Ctrl+C during the command reaches the child via the terminal's own
            // signal delivery to its foreground process group, not through this process.
        };

        if (OperatingSystem.IsWindows())
        {
            // Raw-string form: cmd.exe's own /c parser doesn't follow .NET's ArgumentList
            // quoting convention, so re-quoting here would corrupt embedded quotes.
            psi.Arguments = $"{ShellEnvironment.ShellFlag} {commandText}";
        }
        else
        {
            psi.ArgumentList.Add(ShellEnvironment.ShellFlag);
            psi.ArgumentList.Add(commandText);
        }

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start shell:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        // Share ActiveCts with the same Ctrl+C gate ExecuteAsync's LLM streaming uses (see
        // ReplTurn's OnCancelKeyPress) so an interrupt here is consumed by this wait instead
        // of falling through to the idle-prompt branch, which would otherwise abandon the
        // *next* typed line and print a spurious extra "^C" once this command's own child
        // has already exited from the terminal delivering SIGINT to its process group.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ctx.ActiveCts = cts;
        try
        {
            using (process)
            {
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return;
                }

                // Interrupted: the child already died from the terminal's own SIGINT delivery
                // (a race with cts.Token above, not necessarily losing it) — its exit code is
                // incidental to that, not a real failure worth reporting.
                if (cts.Token.IsCancellationRequested) return;

                if (process.ExitCode != 0)
                    AnsiConsole.MarkupLine($"[dim]  ↳ exit {process.ExitCode}[/]");
            }
        }
        finally
        {
            ctx.ActiveCts = null;
        }
    }
}
