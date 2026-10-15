using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace fuseraft.Cli.Tui;

// Ctrl+F to find text in the transcript, and Ctrl+R to search earlier messages.
internal sealed partial class ReplTui
{
    // While Ctrl+F is open it has the keys: typing narrows the search, Enter or ↑ goes to an older match and ↓ to a
    // newer one, and Esc closes it where it is. PgUp/PgDn still scroll.
    bool FindKey(Key key, string find)
    {
        if (key == Key.Esc)
            CloseFind();
        else if (key.KeyCode == KeyCode.Enter || key == Key.CursorUp || key.KeyCode == (KeyCode.F | KeyCode.CtrlMask))
            Find(older: true);
        else if (key == Key.CursorDown)
            Find(older: false);
        else if (key == Key.PageUp || key == Key.PageDown)
            _view.Page(key == Key.PageUp ? -1 : 1);
        else if (key == Key.Backspace)
        {
            if (find.Length > 0)
                (_find, _found) = (find[..^1], null);
            Find(older: null);
        }
        else if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value is >= ' ' and var rune)
        {
            (_find, _found) = (find + char.ConvertFromUtf32(rune), null);
            Find(older: null);
        }
        return true;
    }

    // Moves to the next match older or newer than the one it's on; null starts again from the newest.
    void Find(bool? older)
    {
        var find = _find ?? "";
        var matches = find.Length == 0 ? [] : Matches(_transcript.Lines(), find);
        if (matches.Count > 0)
        {
            var at = _found is { } found ? matches.IndexOf(found) : -1;
            at = older switch
            {
                null => matches.Count - 1,
                _ when at < 0 => matches.Count - 1,
                true => Math.Max(0, at - 1),
                false => Math.Min(matches.Count - 1, at + 1),
            };
            _found = matches[at];
            _view.ShowLine(matches[at]);
            _rule.Note = $" find: {find} · {at + 1} of {matches.Count} · Enter older · ↓ newer · Esc to close ";
        }
        else
            _rule.Note = find.Length == 0 ? " find: type to search the conversation · Esc to close " : $" find: {find} · no matches · Esc to close ";
        _view.Highlight = find.Length == 0 ? null : find;
    }

    /// <summary>The transcript lines holding <paramref name="find"/>, ignoring case, oldest first.</summary>
    internal static List<int> Matches(List<List<Span>> lines, string find) =>
        [.. lines.Select((line, i) => (Text: string.Concat(line.Select(s => s.Text)), i))
            .Where(l => l.Text.Contains(find, StringComparison.OrdinalIgnoreCase)).Select(l => l.i)];

    void OpenFind()
    {
        _find = "";
        Find(older: null);
    }

    void CloseFind()
    {
        (_find, _found) = (null, null);
        _view.Highlight = null;
        _rule.Note = null;
    }

    // Ctrl+R: earlier messages, newest first, filtered as the user types; the chosen one replaces the draft. A
    // multi-line message is shown on one line, and matched on all of it.
    async Task SearchHistoryAsync()
    {
        Dictionary<string, string> messages = [];
        foreach (var message in _prompt.History.Reverse())
            messages.TryAdd(message.ReplaceLineEndings(" ⏎ "), message);
        if (messages.Count == 0)
        {
            _status.Flash("No messages to search yet");
            return;
        }
        if (await ChooseAsync("History, newest first; type to search", [.. messages.Keys], CancellationToken.None, quiet: false) is { } chosen)
            Ui(() => _prompt.Replace(messages[chosen]));
    }
}
