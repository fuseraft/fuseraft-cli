using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Cli.Tui;

namespace fuseraft.Cli.Commands.Repl;

// /copy, /theme and /dock: the reply on the clipboard, and how the full-screen REPL looks.
internal static partial class ReplCommands
{
    private static async Task<CommandResult> CmdCopyAsync(ReplSessionContext ctx, string arg)
    {
        if (arg is not ("" or "code"))
        {
            AnsiConsole.MarkupLine("[yellow]Usage:[/] /copy, or /copy code for a code block");
            return CommandResult.Continue;
        }
        var reply = ctx.History.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Text.Trim().Length > 0)?.Text.Trim();
        if (reply is null)
        {
            AnsiConsole.MarkupLine("[dim]Nothing to copy yet.[/]");
            return CommandResult.Continue;
        }
        var (text, name) = (reply, "the last reply");
        if (arg == "code")
        {
            var blocks = Markdown.CodeBlocks(reply);
            if (blocks.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]The last reply has no code blocks.[/]");
                return CommandResult.Continue;
            }
            var pick = 0;
            if (blocks.Count > 1)
            {
                var choices = blocks.Select((b, i) => $"{i + 1}. {(b.Language.Length > 0 ? b.Language + ": " : "")}{Truncate(b.Code.Split('\n')[0].Trim(), 60)}").ToList();
                if (await ReplInput.SelectAsync("Which code block?", choices) is not { } chosen)
                    return CommandResult.Continue;
                pick = choices.IndexOf(chosen);
            }
            (text, name) = (blocks[pick].Code, blocks.Count == 1 ? "the code block" : $"code block {pick + 1}");
        }
        var lines = text.Split('\n').Length;
        string? tool;
        if (ReplInput.Screen is { } screen)
            tool = screen.Copy(text);
        else if (ctx.JsonMode)
        {
            // Bridge events bypass the command's captured output, so the panel gets this one directly.
            ReplJsonBridge.Emit(new { type = "copy", text });
            tool = "VS Code";
        }
        else
        {
            if (!Console.IsOutputRedirected)
            {
                Console.Out.Write(Clipboard.Osc52(text));
                Console.Out.Flush();
            }
            tool = Clipboard.CopyNatively(text);
        }
        AnsiConsole.MarkupLine($"[dim]Copied {name} ({lines} line{(lines == 1 ? "" : "s")})" +
                               (tool is null ? " via OSC 52, if the terminal supports it." : ".") + "[/]");
        return CommandResult.Continue;
    }

    private static CommandResult CmdTheme(string arg)
    {
        if (ReplInput.Screen is not { } screen)
        {
            AnsiConsole.MarkupLine(ReplInput.Bridge is not null
                ? "[dim]The VS Code panel follows your VS Code color theme.[/]"
                : "[dim]Themes color the full-screen REPL; this one is running with --plain or piped input.[/]");
            return CommandResult.Continue;
        }
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Theme files are read again each time, so a change to one shows on the next /theme.
        var (files, warnings) = Theme.Load(Theme.Dir);
        List<Theme> themes = [.. Theme.BuiltIn, .. files];
        switch (parts)
        {
            case []:
                var width = themes.Max(t => t.Name.Length) + 2;
                var writer = AnsiConsole.Profile.Out.Writer;
                foreach (var theme in themes)
                    writer.WriteLine((theme.Name == Theme.Current.Name ? Paint(Theme.Current.Accent, "› ") : "  ") + Swatch(theme, width) + "  " + Paint(Theme.Current.Muted, theme.Description));
                foreach (var warning in warnings)
                    AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(warning)}[/]");
                AnsiConsole.MarkupLine($"[dim]Theme files go in {Markup.Escape(Theme.Dir)}; /theme save <name> starts one from the current colors.[/]");
                break;
            case ["save"]:
                AnsiConsole.MarkupLine("[yellow]Name the theme:[/] /theme save <name>.");
                break;
            case ["save", var saveAs]:
                var (file, problem) = Theme.Save(Theme.Chosen, saveAs, Theme.Dir);
                AnsiConsole.MarkupLine(file is not null
                    ? $"[dim]Saved {Markup.Escape(file)}. Change it, then /theme {Markup.Escape(saveAs)} to see it.[/]"
                    : $"[red]✗ {Markup.Escape(problem!)}[/]");
                break;
            case [var name] when Theme.Named(name, themes) is { } named:
                Theme.Current = named;
                screen.Repaint();
                foreach (var warning in warnings.Where(w => w.StartsWith($"themes/{named.Name}.", StringComparison.OrdinalIgnoreCase)))
                    AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(warning)}[/]");
                AnsiConsole.MarkupLine($"[dim]Theme: {Markup.Escape(named.Name)}, for this session. To keep it:[/] fuseraft settings set repl.theme {Markup.Escape(named.Name)}");
                break;
            default:
                AnsiConsole.MarkupLine($"[red]✗ Unknown theme {Markup.Escape(arg)}.[/] [dim]Themes: {Markup.Escape(string.Join(", ", themes.Select(t => t.Name)))}.[/]");
                break;
        }
        return CommandResult.Continue;
    }

    private static CommandResult CmdDock(string arg)
    {
        if (ReplInput.Screen is not { } screen)
        {
            AnsiConsole.MarkupLine(ReplInput.Bridge is not null
                ? "[dim]The VS Code panel's prompt stays at the bottom.[/]"
                : "[dim]Only the full-screen REPL has a prompt to move; this one is running with --plain or piped input.[/]");
            return CommandResult.Continue;
        }
        if (arg.Length == 0)
        {
            foreach (var (_, position, description) in Docks.All)
                AnsiConsole.MarkupLine((position == screen.Docked ? "[bold cyan]› [/]" : "  ") + position.PadRight(8) + $"[dim]{Markup.Escape(description)}[/]");
            return CommandResult.Continue;
        }
        AnsiConsole.MarkupLine(screen.Dock(arg)
            ? $"[dim]Prompt: {Markup.Escape(arg.ToLowerInvariant())}, for this session. To keep it:[/] fuseraft settings set repl.dock {Markup.Escape(arg.ToLowerInvariant())}"
            : $"[red]✗ Unknown position {Markup.Escape(arg)}.[/] [dim]Positions: {Docks.Names}.[/]");
        return CommandResult.Continue;
    }

    // A theme's name in its accent, and a dot in each of its main colors, for choosing between themes.
    private static string Swatch(Theme theme, int width)
    {
        var shown = Theme.TrueColor ? theme : theme.To256();
        return Paint(shown.Accent, theme.Name) + new string(' ', Math.Max(1, width - theme.Name.Length))
            + string.Concat(new[] { shown.Accent, shown.Tool, shown.Agent, shown.Success, shown.Warning, shown.Error, shown.String, shown.Keyword }.Select(c => Paint(c, "●")));
    }

    private static string Paint(string code, string s) => code.Length == 0 ? s : $"\e[{code}m{s}\e[0m";
}
