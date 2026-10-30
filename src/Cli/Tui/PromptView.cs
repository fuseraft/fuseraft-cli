using fuseraft.Cli.Commands.Repl;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

namespace fuseraft.Cli.Tui;

/// <summary>
/// The message being written: Enter sends, Alt+Enter or Shift+Enter starts a new line, ↑/↓ at the edges walk history.
/// While it suggests completions (a slash command, a $skill), ↑/↓ choose one and Tab or Enter takes it.
/// </summary>
internal sealed class PromptView : Editor
{
    readonly List<string> _history = [];
    int _index;
    string _draft = "";
    int _start; // where the text the suggestions would replace begins
    string? _dismissed; // the draft as it was when Esc hid the suggestions; they stay hidden until it changes

    public PromptView()
    {
        Multiline = true;
        WordWrap = true;
        GutterOptions = GutterOptions.None;
    }

    public PromptCompletion? Completion { get; set; }

    /// <summary>On ↑ in an empty prompt: the last message queued during the turn, taken back to edit, or null.</summary>
    public Func<string?>? Unqueue { get; set; }

    public IReadOnlyList<Suggestion> Suggestions { get; private set; } = [];

    public int Selected { get; private set; }

    public event Action? SuggestionsChanged;

    public event Action<string>? Sent;

    /// <summary>Messages sent so far, oldest first.</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>Works out the suggestions for the text before the caret.</summary>
    public void Suggest()
    {
        var typed = Document is null ? "" : Text[..Math.Min(CaretOffset, Text.Length)];
        var (start, items) = typed == _dismissed || Completion is null ? (0, []) : Completion.Suggest(typed);
        if (items.SequenceEqual(Suggestions) && start == _start)
            return;
        (_start, Suggestions, Selected) = (start, items, 0);
        SuggestionsChanged?.Invoke();
    }

    public void Dismiss()
    {
        if (Suggestions.Count == 0)
            return;
        _dismissed = Text[..Math.Min(CaretOffset, Text.Length)];
        Suggest();
    }

    // Replaces what's typed from where the suggestion starts up to the caret.
    void Accept()
    {
        var item = Suggestions[Selected].Text;
        Document!.Replace(_start, CaretOffset - _start, item);
        CaretOffset = _start + item.Length;
        Suggest();
    }

    /// <summary>Puts <paramref name="text"/> in place of the draft, the caret at its end.</summary>
    public void Replace(string text)
    {
        Text = text;
        CaretOffset = text.Length;
        _index = _history.Count;
    }

    public void Remember(string text)
    {
        if (text.Length > 0 && (_history.Count == 0 || _history[^1] != text))
            _history.Add(text);
        _index = _history.Count;
    }

    /// <summary>Rows the draft takes when wrapped to <paramref name="width"/>.</summary>
    public int Rows(int width) =>
        Text.Split('\n').Sum(line => Math.Max(1, (line.GetColumns() + Math.Max(1, width) - 1) / Math.Max(1, width)));

    protected override bool OnKeyDown(Key key)
    {
        // Typing that came just before this key may not have been looked at yet.
        if (Suggestions.Count > 0)
            Suggest();
        if (Suggestions.Count > 0)
        {
            if (key == Key.CursorUp || key == Key.CursorDown)
            {
                Selected = (Selected + (key == Key.CursorUp ? -1 : 1) + Suggestions.Count) % Suggestions.Count;
                SuggestionsChanged?.Invoke();
                return true;
            }
            if (key == Key.Tab || key.KeyCode == KeyCode.Enter)
            {
                Accept();
                return true;
            }
        }
        var handled = Handle(key);
        Suggest(); // the caret may have moved without the text changing
        return handled;
    }

    bool Handle(Key key)
    {
        if (key.KeyCode == KeyCode.Enter)
        {
            var text = Text.Trim();
            Text = "";
            _draft = "";
            if (text.Length > 0)
                Sent?.Invoke(text);
            return true;
        }
        if (Document is not { } document)
            return base.OnKeyDown(key);
        // Alt+Enter often arrives as Ctrl+Alt+M, since Enter is a carriage return.
        if (key.KeyCode is (KeyCode.Enter | KeyCode.ShiftMask) or (KeyCode.Enter | KeyCode.AltMask) or (KeyCode.M | KeyCode.CtrlMask | KeyCode.AltMask))
        {
            Insert("\n");
            return true;
        }
        var line = document.GetLineByOffset(CaretOffset).LineNumber;
        if (key.KeyCode == (KeyCode.U | KeyCode.CtrlMask))
        {
            var start = document.GetLineByOffset(CaretOffset).Offset;
            document.Remove(start, CaretOffset - start);
            return true;
        }
        if (key == Key.CursorUp && Text.Length == 0 && Unqueue?.Invoke() is { } queued)
        {
            Replace(queued);
            return true;
        }
        if (key == Key.CursorUp && line == 1 && _index > 0)
            return Recall(_index - 1);
        if (key == Key.CursorDown && line == document.LineCount && _index < _history.Count)
            return Recall(_index + 1);
        return base.OnKeyDown(key);
    }

    public void Insert(string text)
    {
        var at = CaretOffset;
        Document?.Insert(at, text);
        CaretOffset = at + text.Length;
    }

    bool Recall(int index)
    {
        if (_index == _history.Count)
            _draft = Text;
        _index = index;
        Text = index == _history.Count ? _draft : _history[index];
        CaretOffset = Text.Length;
        return true;
    }
}

/// <summary>A completion the prompt offers: the text it puts in, and a few words on what it is.</summary>
internal sealed record Suggestion(string Text, string Note = "");

/// <summary>
/// Completes slash commands while the first word of the draft is one, then the words some of them take (a subcommand, a
/// theme, a dock), $skill names, and workspace paths after an @.
/// </summary>
internal sealed class PromptCompletion
{
    /// <summary>Rows the list takes at most; it scrolls to keep the chosen one in view.</summary>
    public const int MaxShown = 8;

    /// <summary>The session's skills, for $name.</summary>
    public Func<IReadOnlyList<string>>? Skills { get; set; }

    public FileIndex? Files { get; set; }

    /// <summary>
    /// What could replace the end of <paramref name="typed"/> (the draft up to the caret): the offset the replaced
    /// text starts at, and the replacements, "/help" for "/he", "on" for "/hitl o", "$review" for "$re", or "@src/"
    /// for "@s".
    /// </summary>
    public (int Start, List<Suggestion> Items) Suggest(string typed)
    {
        if (typed.StartsWith('/') && !typed.Contains('\n'))
        {
            var space = typed.IndexOf(' ');
            if (space < 0)
                return (0, [.. ReplLineReader.SlashCommands
                    .Where(c => c.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && c != typed)
                    .Select(c => new Suggestion(c, ReplCommands.Describe(c)))]);
            var rest = typed[(space + 1)..];
            if (rest.Contains(' '))
                return (0, []);
            return (space + 1, [.. Arguments(typed[..space])
                .Where(s => s.Text.StartsWith(rest, StringComparison.OrdinalIgnoreCase) && s.Text != rest)]);
        }
        if (typed.StartsWith('$') && !typed.Contains(' ') && !typed.Contains('\n'))
            return (0, [.. (Skills?.Invoke() ?? [])
                .Select(s => "$" + s)
                .Where(s => s.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && s != typed)
                .Select(s => new Suggestion(s))]);
        var start = typed.LastIndexOfAny([' ', '\n', '\t']) + 1;
        if (Files is null || start >= typed.Length || typed[start] != '@')
            return (0, []);
        return (start, [.. Files.Complete(typed[(start + 1)..]).Select(p => new Suggestion("@" + p))]);
    }

    // The word a command takes: its subcommands, or a theme, a dock, or what /copy copies.
    static IEnumerable<Suggestion> Arguments(string command) => command.ToLowerInvariant() switch
    {
        "/theme" => [.. Theme.All().Select(t => new Suggestion(t.Name, t.Description)), new("save", "save the current colors as a theme file")],
        "/dock" => Docks.All.Select(d => new Suggestion(d.Name, d.Description)),
        "/copy" => [new("code", "a code block from the last reply")],
        _ => ReplLineReader.SubCommands.TryGetValue(command, out var subs) ? subs.Select(s => new Suggestion(s)) : [],
    };
}
