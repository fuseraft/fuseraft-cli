namespace fuseraft.Cli.Tui;

/// <summary>An arrow-key list: ↑/↓ to move, type to filter, Enter to choose, Esc to cancel.</summary>
internal sealed class Picker
{
    public const int DefaultHeight = 10;

    readonly IReadOnlyList<string> choices;
    readonly bool allowTyped;
    readonly int height;

    public Picker(IReadOnlyList<string> choices, string? selected = null, bool allowTyped = false, int height = DefaultHeight)
    {
        this.choices = choices;
        this.allowTyped = allowTyped;
        this.height = height;
        Matches = choices;
        Move(selected is null ? 0 : Math.Max(0, IndexOf(choices, selected)));
    }

    public enum Outcome { Continue, Chosen, Cancelled }

    string _filter = "";
    int _index;
    int _top;

    public string Filter => _filter;

    public IReadOnlyList<string> Matches { get; private set; }

    /// <summary>What Enter picked: the highlighted match, or the typed filter when nothing matches and typing is allowed.</summary>
    public string? Result { get; private set; }

    public Outcome Handle(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Result = Matches.Count > 0 ? Matches[_index]
                    : allowTyped && _filter.Trim().Length > 0 ? _filter.Trim()
                    : null;
                return Result is null ? Outcome.Continue : Outcome.Chosen;
            case ConsoleKey.Escape:
                return Outcome.Cancelled;
            case ConsoleKey.C or ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                return Outcome.Cancelled;
            case ConsoleKey.UpArrow:
                Move(_index == 0 ? Matches.Count - 1 : _index - 1);
                break;
            case ConsoleKey.DownArrow:
                Move(_index == Matches.Count - 1 ? 0 : _index + 1);
                break;
            case ConsoleKey.PageUp:
                Move(Math.Max(0, _index - height));
                break;
            case ConsoleKey.PageDown:
                Move(Math.Min(Matches.Count - 1, _index + height));
                break;
            case ConsoleKey.Home:
                Move(0);
                break;
            case ConsoleKey.End:
                Move(Matches.Count - 1);
                break;
            case ConsoleKey.Backspace when _filter.Length > 0:
                SetFilter(_filter[..^1]);
                break;
            default:
                if (!char.IsControl(key.KeyChar))
                    SetFilter(_filter + key.KeyChar);
                break;
        }
        return Outcome.Continue;
    }

    /// <summary>The lines to draw: the filter, then the visible window of matches with the highlighted one marked.</summary>
    public List<(string Text, bool Highlighted)> Lines()
    {
        var lines = new List<(string, bool)>();
        if (_filter.Length > 0 || choices.Count > height)
            lines.Add(($"  Filter: {_filter}", false));
        if (Matches.Count == 0)
            lines.Add((allowTyped && _filter.Trim().Length > 0 ? $"  No match. Enter uses \"{_filter.Trim()}\"." : "  No match.", false));
        if (_top > 0)
            lines.Add(($"  ↑ {_top} more", false));
        var end = Math.Min(Matches.Count, _top + height);
        for (var i = _top; i < end; i++)
            lines.Add((i == _index ? $"› {Matches[i]}" : $"  {Matches[i]}", i == _index));
        if (end < Matches.Count)
            lines.Add(($"  ↓ {Matches.Count - end} more", false));
        return lines;
    }

    void SetFilter(string filter)
    {
        var current = Matches.Count > 0 ? Matches[_index] : null;
        _filter = filter;
        Matches = [.. choices.Where(c => c.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        _top = 0;
        // Keep the highlighted item when it still matches; otherwise start from the top.
        Move(current is null ? 0 : Math.Max(0, IndexOf(Matches, current)));
    }

    void Move(int index)
    {
        if (Matches.Count == 0)
        {
            _index = 0;
            return;
        }
        _index = Math.Clamp(index, 0, Matches.Count - 1);
        if (_index < _top)
            _top = _index;
        else if (_index >= _top + height)
            _top = _index - height + 1;
    }

    static int IndexOf(IReadOnlyList<string> list, string item)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == item)
                return i;
        return -1;
    }
}
