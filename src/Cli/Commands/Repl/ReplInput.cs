using Spectre.Console;
using fuseraft.Core.Interfaces;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>The status line's view of the session, between and during turns.</summary>
/// <param name="Context">How full the context is, as a fraction of the budget.</param>
/// <param name="Level">How near auto-compaction is.</param>
internal sealed record ReplStatus(string Model, int Turn, double Context, ContextLevel Level, IReadOnlyList<string> Modes);

internal enum ContextLevel { Clear, Near, Compacting }

/// <summary>What the REPL needs from the full-screen TUI: input, questions in place of the prompt, the status line, and a turn's events.</summary>
internal interface IReplScreen
{
    /// <summary>Called on Ctrl+C; returns true when it cancelled something (a turn, a shell command).</summary>
    Func<bool>? Interrupt { set; }

    /// <summary>Takes a message the user sends while a turn runs; true when it joined the turn. Set for the length of a turn.</summary>
    Func<string, bool>? Interject { set; }

    /// <summary>The next message the user sends; null ends the session.</summary>
    Task<string?> ReadAsync(CancellationToken ct);

    void Status(ReplStatus status);

    /// <summary>Columns the transcript has, for Spectre's layout.</summary>
    int Width { get; }

    /// <summary>One of <paramref name="keys"/>; Esc answers the first.</summary>
    Task<char> KeyAsync(string prompt, string keys, CancellationToken ct);

    /// <summary>A line of text; null when dismissed with Esc.</summary>
    Task<string?> TextAsync(string prompt, bool secret, CancellationToken ct);

    /// <summary>A pick from a list; null when dismissed with Esc.</summary>
    Task<string?> ChooseAsync(string title, IReadOnlyList<string> choices, CancellationToken ct);

    /// <summary>The next piece of the model's reply, styled as Markdown as it streams.</summary>
    void Text(string delta);

    /// <summary>The next piece of the model's thinking.</summary>
    void Thinking(string delta);

    /// <summary>Ends the reply being streamed; with <paramref name="turn"/>, the turn too.</summary>
    void EndMessage(bool turn);

    /// <summary>A tool call starting; <paramref name="arguments"/> is the whole of them, for Ctrl+O.</summary>
    void ToolStarted(string callId, string name, string summary, string? agent = null, string? arguments = null);

    void ToolFinished(string callId, string name, bool ok, string result, string? agent = null);

    /// <summary>A file the turn changed, with the lines it added and removed when they're known.</summary>
    void FileChanged(string path, int? added, int? removed);

    /// <summary>Keeps a whole output (an approval's diff) for Ctrl+O.</summary>
    void Keep(string title, string text, bool diff);

    /// <summary>The session is ending: say so, and take no more input.</summary>
    void Ending();

    /// <summary>Puts <paramref name="text"/> on the clipboard; returns the system tool's name, or null when only the terminal was asked.</summary>
    string? Copy(string text);

    /// <summary>Where the prompt is: "bottom" or "top".</summary>
    string Docked { get; }

    /// <summary>Moves the prompt; false for a position it doesn't know.</summary>
    bool Dock(string position);

    /// <summary>Draws everything again in the current theme.</summary>
    void Repaint();
}

/// <summary>
/// Questions the REPL asks the user: in the TUI's panel when it's running, otherwise on the plain terminal with
/// <c>Console.ReadLine</c> and Spectre's prompts. Prompts are Spectre markup; the TUI shows them as plain text, and
/// writes the question and its answer to the transcript the way the terminal would have shown them.
/// </summary>
internal static class ReplInput
{
    /// <summary>The running TUI, or null on a plain terminal.</summary>
    internal static IReplScreen? Screen { get; set; }

    /// <summary>The VS Code panel's stdin, when the REPL runs behind it; picks are asked there as questions.</summary>
    internal static ReplStdinPump? Bridge { get; set; }

    /// <summary>A line of text after <paramref name="prompt"/>; null when stdin closes or the TUI's panel is dismissed.</summary>
    internal static string? ReadLine(string prompt)
    {
        if (Screen is not { } screen)
        {
            AnsiConsole.Markup(prompt);
            return Console.ReadLine();
        }
        var answer = screen.TextAsync(Plain(prompt), secret: false, CancellationToken.None).GetAwaiter().GetResult();
        AnsiConsole.MarkupLine(prompt + Markup.Escape(answer ?? ""));
        return answer;
    }

    /// <summary>A y/N question: true only for y or yes.</summary>
    internal static bool Confirm(string prompt)
    {
        if (Screen is null)
        {
            var input = ReadLine(prompt)?.Trim() ?? string.Empty;
            return input.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                   input.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        var question = System.Text.RegularExpressions.Regex.Replace(Plain(prompt), @"\s*\(y/N\):?$", "");
        return Approve(question, alwaysLabel: null) == ApprovalAnswer.Yes;
    }

    /// <summary>
    /// An approval: yes, no, or (when <paramref name="alwaysLabel"/> isn't null) always, for what the label describes.
    /// The TUI takes one key, and ignores keys typed as it appears; a plain terminal reads y, a (always) or anything
    /// else for no.
    /// </summary>
    internal static ApprovalAnswer Approve(string question, string? alwaysLabel)
    {
        if (Screen is not { } screen)
        {
            var input = ReadLine($"[dim]{Markup.Escape(question)} (y/N{(alwaysLabel is null ? "" : "/a")}):[/]  " +
                                 (alwaysLabel is null ? "" : $"[dim](a = always: {Markup.Escape(alwaysLabel)})[/]  "))?.Trim() ?? string.Empty;
            return input.ToLowerInvariant() switch
            {
                "y" or "yes" => ApprovalAnswer.Yes,
                "a" or "always" when alwaysLabel is not null => ApprovalAnswer.Always,
                _ => ApprovalAnswer.No,
            };
        }
        var prompt = $"\e[33m{question}\e[0m \e[1m[y]\e[0mes \e[1m[n]\e[0mo"
                     + (alwaysLabel is null ? "" : $" \e[1m[a]\e[0mlways\e[2m: {alwaysLabel}\e[0m");
        var key = screen.KeyAsync(prompt, alwaysLabel is null ? "ny" : "nya", CancellationToken.None).GetAwaiter().GetResult();
        var answer = key switch { 'y' => ApprovalAnswer.Yes, 'a' => ApprovalAnswer.Always, _ => ApprovalAnswer.No };
        AnsiConsole.MarkupLine($"[dim]  {Markup.Escape(question)}[/] " + answer switch
        {
            ApprovalAnswer.Yes    => "[green]yes[/]",
            ApprovalAnswer.Always => $"[green]always[/][dim]: {Markup.Escape(alwaysLabel!)}[/]",
            _                     => "[red]no[/]",
        });
        return answer;
    }

    /// <summary>
    /// A line of text, <paramref name="defaultValue"/> when left empty; asked again when it's empty and required, as
    /// Spectre's TextPrompt does. Null when the TUI's panel is dismissed.
    /// </summary>
    internal static string? Ask(string prompt, string? defaultValue = null, bool secret = false, bool allowEmpty = false)
    {
        if (Screen is not { } screen)
        {
            var textPrompt = new TextPrompt<string>(prompt).PromptStyle("white");
            if (allowEmpty) textPrompt.AllowEmpty();
            if (secret) textPrompt.Secret('•');
            if (defaultValue is not null) textPrompt.DefaultValue(defaultValue);
            return AnsiConsole.Prompt(textPrompt);
        }
        var shown = Plain(prompt) + (defaultValue is null || secret ? "" : $" ({defaultValue})");
        while (true)
        {
            var answer = screen.TextAsync(shown, secret, CancellationToken.None).GetAwaiter().GetResult();
            if (answer is null)
            {
                AnsiConsole.MarkupLine(prompt + ": [dim](cancelled)[/]");
                return null;
            }
            if (answer.Length == 0 && defaultValue is not null)
                answer = defaultValue;
            if (answer.Length == 0 && !allowEmpty)
                continue;
            AnsiConsole.MarkupLine(prompt + ": " + Markup.Escape(secret ? new string('•', Math.Min(answer.Length, 8)) : answer));
            return answer;
        }
    }

    /// <summary>One of <paramref name="choices"/>; null when the TUI's panel is dismissed. The caller reports what was chosen.</summary>
    internal static async Task<string?> SelectAsync(string title, IReadOnlyList<string> choices, CancellationToken ct = default)
    {
        if (Screen is null && Bridge is { } bridge)
        {
            ReplJsonBridge.Emit(new { type = "question", question = Plain(title), options = choices, allowOther = false });
            var picked = await bridge.ReadQuestionResponseAsync(ct);
            return picked is not null && choices.Contains(picked) ? picked : null;
        }
        if (Screen is not { } screen)
            return await new SelectionPrompt<string>()
                .Title(title)
                .PageSize(15)
                .MoreChoicesText("[dim](Move up/down to see more)[/]")
                .UseConverter(Markup.Escape)
                .AddChoices(choices)
                .ShowAsync(AnsiConsole.Console, ct);
        AnsiConsole.MarkupLine(title);
        var answer = await screen.ChooseAsync(Plain(title), choices, ct);
        if (answer is null)
            AnsiConsole.MarkupLine("[dim]  (cancelled)[/]");
        return answer;
    }

    internal static string? Select(string title, IReadOnlyList<string> choices) =>
        SelectAsync(title, choices).GetAwaiter().GetResult();

    static string Plain(string markup) => Markup.Remove(markup).Trim();
}
