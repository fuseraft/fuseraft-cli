using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace fuseraft.Cli.Tui;

/// <summary>The transcript, word-wrapped to the view's width. It follows new output unless the user has scrolled up.</summary>
sealed class TranscriptView(Transcript transcript) : View
{
    readonly List<List<Span>> _rows = [];
    readonly List<int> _starts = []; // the row each transcript line starts on
    int _width = -1;
    int _version = -1;
    int _removals;
    int _top;
    bool _follow = true;
    int _reported;

    /// <summary>Raised when <see cref="Below"/> changes: on a scroll, or when output arrives or goes while scrolled up.</summary>
    public Action? Scrolled { get; set; }

    string? _highlight;

    /// <summary>Text drawn in reverse wherever it appears, ignoring case: what Ctrl+F is looking for.</summary>
    public string? Highlight
    {
        get => _highlight;
        set
        {
            _highlight = value;
            SetNeedsDraw();
        }
    }

    /// <summary>Scrolls so transcript line <paramref name="line"/> sits a third of the way down.</summary>
    public void ShowLine(int line)
    {
        Sync();
        if (line >= _starts.Count)
            return;
        var last = Math.Max(0, _rows.Count - Viewport.Height);
        _top = Math.Clamp(_starts[line] - Viewport.Height / 3, 0, last);
        _follow = _top >= last;
        SetNeedsDraw();
        Scrolled?.Invoke();
    }

    /// <summary>Raised when the user scrolls with the mouse.</summary>
    public Action? Wheeled { get; set; }

    /// <summary>Rows below the bottom of the view, when the user has scrolled up.</summary>
    public int Below => _follow ? 0 : Math.Max(0, _rows.Count - _top - Viewport.Height);

    public void Page(int direction) => ScrollBy(direction * Math.Max(1, Viewport.Height - 2));

    public void ScrollToTop()
    {
        _follow = false;
        _top = 0;
        SetNeedsDraw();
        Scrolled?.Invoke();
    }

    public void Follow()
    {
        _follow = true;
        SetNeedsDraw();
        Scrolled?.Invoke();
    }

    public void ScrollBy(int rows)
    {
        Sync();
        var last = Math.Max(0, _rows.Count - Viewport.Height);
        _top = Math.Clamp((_follow ? last : _top) + rows, 0, last);
        _follow = _top >= last;
        SetNeedsDraw();
        Scrolled?.Invoke();
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
            ScrollBy(-3);
        else if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
            ScrollBy(3);
        else
            return false;
        Wheeled?.Invoke();
        return true;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        Sync();
        var height = Viewport.Height;
        var plain = Styled.Plain;
        // Following, or the transcript shrank (a hidden diff) under where the user had scrolled to.
        _top = _follow ? Math.Max(0, _rows.Count - height) : Math.Min(_top, Math.Max(0, _rows.Count - height));
        for (var i = 0; i < height; i++)
        {
            Move(0, i);
            var used = 0;
            var row = _top + i < _rows.Count ? _rows[_top + i] : [];
            foreach (var (text, style, marked) in Marked(row, _highlight))
            {
                var attribute = Styled.Of(style, plain);
                SetAttribute(marked ? Styled.Marked(attribute) : attribute);
                AddStr(text);
                used += text.GetColumns();
            }
            // A row that ends in an empty span is filled with its background, as a code block is.
            SetAttribute(row is [.., { Text.Length: 0 } fill] ? Styled.Of(fill.Style, plain) : plain);
            if (used < Viewport.Width)
                AddStr(new string(' ', Viewport.Width - used));
        }
        if (Below != _reported)
        {
            _reported = Below;
            Scrolled?.Invoke();
        }
        return true;
    }

    /// <summary>A row's spans, split where <paramref name="find"/> starts and ends, each piece marked when it's part of a match.</summary>
    internal static IEnumerable<(string Text, Style Style, bool Marked)> Marked(List<Span> row, string? find)
    {
        var text = string.Concat(row.Select(s => s.Text));
        var marked = new bool[text.Length];
        if (!string.IsNullOrEmpty(find))
            for (var at = text.IndexOf(find, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(find, at + find.Length, StringComparison.OrdinalIgnoreCase))
                Array.Fill(marked, true, at, find.Length);
        var pos = 0;
        foreach (var span in row)
        {
            for (int start = 0, end; start < span.Text.Length; start = end)
            {
                var m = marked[pos + start];
                for (end = start; end < span.Text.Length && marked[pos + end] == m; end++) { }
                yield return (span.Text[start..end], span.Style, m);
            }
            pos += span.Text.Length;
        }
    }

    // Rewraps everything when the width changes or lines were taken out; otherwise only from the first line that may
    // have changed: the last one (which may have grown), or the start of a streamed message that was redrawn.
    void Sync()
    {
        var width = Viewport.Width;
        if (width == _width && transcript.Version == _version && transcript.Removals == _removals)
            return;
        if (width != _width || transcript.Removals != _removals)
        {
            _width = width;
            _removals = transcript.Removals;
            _rows.Clear();
            _starts.Clear();
        }
        var (version, from, lines) = transcript.Since(_starts.Count);
        _version = version;
        if (from < _starts.Count)
        {
            _rows.RemoveRange(_starts[from], _rows.Count - _starts[from]);
            _starts.RemoveRange(from, _starts.Count - from);
        }
        foreach (var line in lines)
        {
            _starts.Add(_rows.Count);
            _rows.AddRange(Transcript.Wrap(line, width));
        }
    }
}
