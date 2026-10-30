using System.Text;
using Terminal.Gui.Text;

namespace fuseraft.Cli.Tui;

/// <summary>
/// How a run of transcript text looks: a foreground and a background color (30-37 or 90-97 for the terminal's 16, 256 + n
/// for 256-color n, <see cref="Style.Rgb"/> + 0xrrggbb for a 24-bit color, or 0 for the default), bold, dim, italic,
/// underline. An empty span with a background at the end of a line fills the rest of each of its rows, as \e[K does.
/// </summary>
internal readonly record struct Style(int Color, bool Bold = false, bool Dim = false, bool Italic = false, bool Underline = false, int Background = 0)
{
    public const int Rgb = 0x1000000;
}

internal readonly record struct Span(string Text, Style Style);

/// <summary>
/// What the TUI shows above the prompt. Spectre writes its ANSI-styled output into it exactly as it would to a terminal,
/// so every command looks the same in both; this reads the SGR codes back into styled lines.
/// </summary>
/// <remarks>
/// A carriage return (or <c>\e[2K</c>) clears the line it's on before the next text lands there, so a transient
/// "saving memory…" that the REPL overwrites with <c>\r</c> disappears here too, and <c>\e[2J</c> empties the
/// transcript. Other cursor movement is ignored. Text written while a message streams ends that message first, so
/// its next redraw can't take the text back out.
/// </remarks>
internal sealed class Transcript : TextWriter
{
    readonly List<List<Span>> _lines = [[]];
    readonly StringBuilder _escape = new();
    Style _style;
    bool _inEscape;
    bool _returned; // a \r was written; the next text replaces the line
    int? _live; // the first line of the message being streamed, which is rewritten as it grows
    int _editedFrom = int.MaxValue; // the first line changed in place since the view last looked

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>Raised after new text arrives, on the writing thread.</summary>
    public event Action? Changed;

    /// <summary>Bumped on every write, so a view knows whether its wrapped copy is stale.</summary>
    public int Version { get; private set; }

    /// <summary>The transcript as ANSI text, for printing to the terminal after the TUI closes.</summary>
    public string Ansi(bool color)
    {
        var sb = new StringBuilder();
        lock (_lines)
            foreach (var (line, i) in _lines.Select((l, i) => (l, i)))
            {
                foreach (var span in line)
                    sb.Append(color && span.Style != default ? Painted(span) : span.Text);
                if (i < _lines.Count - 1 || line.Count > 0)
                    sb.Append('\n');
            }
        return sb.ToString();
    }

    /// <summary>One line of spans as ANSI text.</summary>
    public static string Ansi(List<Span> line) =>
        string.Concat(line.Select(span => span.Style == default ? span.Text : Painted(span)));

    static string Sgr(Style style) =>
        "\e[" + string.Join(';', new[] { style.Bold ? "1" : null, style.Dim ? "2" : null, style.Italic ? "3" : null, style.Underline ? "4" : null, ColorCode(style.Color, 0), ColorCode(style.Background, 10) }.OfType<string>()) + "m";

    // A foreground color's code, or with 10 added (40-47, 100-107, 48;...), a background's.
    static string? ColorCode(int color, int background) => color switch
    {
        >= Style.Rgb => $"{38 + background};2;{(color >> 16) & 255};{(color >> 8) & 255};{color & 255}",
        >= 256 => $"{38 + background};5;{color - 256}",
        > 0 => (color + background).ToString(),
        _ => null,
    };

    static string Painted(Span span) => span.Text.Length == 0 ? $"{Sgr(span.Style)}\e[K\e[0m" : $"{Sgr(span.Style)}{span.Text}\e[0m";

    /// <summary>
    /// Writes a whole line above what's still being streamed (the open line, or the whole message while
    /// <see cref="Stream"/> is redrawing it), so what the user sent mid-sentence doesn't split the model's sentence in two.
    /// Returns the line, for <see cref="Rewrite"/>.
    /// </summary>
    public List<Span> WriteLineAbove(string text)
    {
        List<Span> written;
        lock (_lines)
        {
            var from = _live ?? _lines.Count - 1;
            List<List<Span>> held = _live is null && _lines[^1].Count == 0 ? [] : _lines[from..];
            _lines.RemoveRange(_lines.Count - held.Count, held.Count);
            if (held.Count > 0)
                _lines.Add([]);
            var style = _style;
            _style = default;
            Parse(text + "\n");
            written = _lines[^2];
            if (held.Count > 0)
            {
                _lines.RemoveAt(_lines.Count - 1);
                if (_live is not null)
                    _live = _lines.Count;
                _lines.AddRange(held);
                _style = style;
                _editedFrom = Math.Min(_editedFrom, from);
            }
            Version++;
        }
        Changed?.Invoke();
        return written;
    }

    /// <summary>Writes a whole line of its own, after the last if that isn't empty, and returns it for <see cref="Rewrite"/>.</summary>
    public List<Span> AddLine(string text)
    {
        List<Span> written;
        lock (_lines)
        {
            if (_live is null && _lines[^1].Count > 0)
                _lines.Add([]);
            WriteCore(text + "\n");
            written = _lines[^2];
        }
        Changed?.Invoke();
        return written;
    }

    /// <summary>Redraws <paramref name="line"/> as <paramref name="text"/>, if it's still in the transcript.</summary>
    public void Rewrite(List<Span> line, string text)
    {
        var spans = new Transcript();
        spans.Write(text);
        lock (_lines)
        {
            var at = _lines.FindLastIndex(l => ReferenceEquals(l, line));
            if (at < 0)
                return;
            line.Clear();
            line.AddRange(spans._lines[0]);
            _editedFrom = Math.Min(_editedFrom, at);
            Version++;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Redraws the message being streamed: <paramref name="settled"/> (whole lines) won't change again, and
    /// <paramref name="open"/> replaces whatever was open before. The first call starts the message on the open line.
    /// </summary>
    public void Stream(string settled, string open)
    {
        lock (_lines)
        {
            var from = _live ??= _lines.Count - 1;
            _lines.RemoveRange(from, _lines.Count - from);
            _lines.Add([]);
            _style = default;
            Parse(settled);
            _live = _lines.Count - 1;
            Parse(open);
            _style = default;
            _editedFrom = Math.Min(_editedFrom, from);
            Version++;
        }
        Changed?.Invoke();
    }

    /// <summary>Whether a message is being streamed: <see cref="Stream"/> was called and nothing has ended it since.</summary>
    public bool Streaming
    {
        get
        {
            lock (_lines)
                return _live is not null;
        }
    }

    /// <summary>Ends the message <see cref="Stream"/> was redrawing; what it showed last stays.</summary>
    public void EndStream()
    {
        lock (_lines)
            _live = null;
    }

    /// <summary>Takes every line out, for /clear. Call it through <see cref="Renderer.Clear"/>, so no output is cut in half.</summary>
    public void Clear()
    {
        lock (_lines)
        {
            _lines.Clear();
            _lines.Add([]);
            _style = default;
            _inEscape = _returned = false;
            _live = null;
            _editedFrom = int.MaxValue;
            Version++;
            Removals++;
        }
        Changed?.Invoke();
    }

    public int Count
    {
        get
        {
            lock (_lines)
                return _lines.Count;
        }
    }

    /// <summary>Bumped when lines are removed, so a view knows to rebuild its wrapped copy instead of extending it.</summary>
    public int Removals { get; private set; }

    /// <summary>
    /// Runs <paramref name="write"/>, which must write whole lines, and returns an action that takes those lines back
    /// out. Call it through <see cref="Renderer.Section"/>, whose lock keeps other output from landing among them.
    /// </summary>
    public Action Section(Action write)
    {
        int from;
        lock (_lines)
            from = _live ?? _lines.Count - 1;
        write();
        HashSet<List<Span>> written;
        lock (_lines)
            written = new(_lines[from..(_live ?? _lines.Count - 1)], ReferenceEqualityComparer.Instance);
        return () =>
        {
            lock (_lines)
            {
                var live = _live is int l ? _lines[l] : null;
                if (_lines.RemoveAll(written.Contains) == 0)
                    return;
                if (live is not null)
                    _live = _lines.IndexOf(live);
                Version++;
                Removals++;
            }
            Changed?.Invoke();
        };
    }

    /// <summary>
    /// What a view that has seen <paramref name="seen"/> lines needs to catch up: the first line that may have changed (the
    /// last one it saw, or earlier when the message being streamed was redrawn) and copies of the lines from there on.
    /// </summary>
    public (int Version, int From, List<List<Span>> Lines) Since(int seen)
    {
        lock (_lines)
        {
            var from = Math.Min(Math.Max(0, seen - 1), _editedFrom);
            _editedFrom = int.MaxValue;
            return (Version, from, Lines(from));
        }
    }

    /// <summary>A copy of lines <paramref name="from"/> onward, safe to use while writes continue.</summary>
    public List<List<Span>> Lines(int from = 0)
    {
        lock (_lines)
            return [.. _lines.Skip(from).Select(line => line.ToList())];
    }

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        lock (_lines)
            WriteCore(value);
        Changed?.Invoke();
    }

    void WriteCore(string value)
    {
        if (_live is not null)
        {
            _live = null;
            if (_lines[^1].Count > 0)
                _lines.Add([]);
        }
        Parse(value);
        Version++;
    }

    public override void WriteLine(string? value) => Write(value + "\n");

    public override void WriteLine() => Write("\n");

    void Parse(string value)
    {
        var text = new StringBuilder();
        foreach (var c in value)
        {
            if (_inEscape)
            {
                _escape.Append(c);
                if (c is >= '@' and <= '~' && _escape.Length > 1)
                {
                    _inEscape = false;
                    var sequence = _escape.ToString();
                    if (c == 'm')
                        Apply(sequence[1..^1]);
                    // Erasing to the end of the line fills the rest of it with the background.
                    else if (sequence is "[K" or "[0K" && _style.Background != 0)
                        _lines[^1].Add(new Span("", _style));
                    else if (sequence == "[2K")
                        ClearLine();
                    // Spectre's Clear (for /clear) erases the screen; here that empties the transcript.
                    else if (sequence == "[2J")
                        ClearScreen();
                }
                continue;
            }
            switch (c)
            {
                case '\e':
                    Flush(text);
                    _inEscape = true;
                    _escape.Clear();
                    break;
                case '\n':
                    Flush(text);
                    _returned = false;
                    _lines.Add([]);
                    break;
                case '\r':
                    Flush(text);
                    _returned = true;
                    break;
                default:
                    if (_returned)
                    {
                        _returned = false;
                        ClearLine();
                    }
                    text.Append(c);
                    break;
            }
        }
        Flush(text);
    }

    void ClearScreen()
    {
        _lines.Clear();
        _lines.Add([]);
        _returned = false;
        _live = null;
        _editedFrom = int.MaxValue;
        Removals++;
    }

    void ClearLine()
    {
        if (_lines[^1].Count == 0)
            return;
        _lines[^1].Clear();
        _editedFrom = Math.Min(_editedFrom, _lines.Count - 1);
    }

    void Flush(StringBuilder text)
    {
        if (text.Length == 0)
            return;
        var line = _lines[^1];
        if (line.Count > 0 && line[^1].Style == _style && line[^1].Text.Length > 0)
            line[^1] = new Span(line[^1].Text + text, _style);
        else
            line.Add(new Span(text.ToString(), _style));
        text.Clear();
    }

    // The subset of SGR Spectre, the renderer and Markdown use: reset, bold, dim, italic, underline, their resets, and the
    // foreground and background colors: 16, 256 (38;5;n, 48;5;n) and 24-bit (38;2;r;g;b, 48;2;r;g;b).
    void Apply(string parameters)
    {
        var ps = parameters.Split(';').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        for (var i = 0; i < ps.Length; i++)
        {
            _style = ps[i] switch
            {
                0 => default,
                1 => _style with { Bold = true },
                2 => _style with { Dim = true },
                3 => _style with { Italic = true },
                4 => _style with { Underline = true },
                22 => _style with { Bold = false, Dim = false },
                23 => _style with { Italic = false },
                24 => _style with { Underline = false },
                >= 30 and <= 37 and var color => _style with { Color = color },
                >= 90 and <= 97 and var bright => _style with { Color = bright },
                38 when Extended(ps, i) is { } color => _style with { Color = color },
                39 => _style with { Color = 0 },
                >= 40 and <= 47 or >= 100 and <= 107 => _style with { Background = ps[i] - 10 },
                48 when Extended(ps, i) is { } color => _style with { Background = color },
                49 => _style with { Background = 0 },
                _ => _style,
            };
            if (ps[i] is 38 or 48 && i + 1 < ps.Length)
                i += ps[i + 1] == 5 ? 2 : ps[i + 1] == 2 ? 4 : 1;
        }
    }

    // The color after a 38 or 48 at ps[i]: 5;n or 2;r;g;b.
    static int? Extended(int[] ps, int i) =>
        i + 2 < ps.Length && ps[i + 1] == 5 && ps[i + 2] is >= 0 and <= 255 ? 256 + ps[i + 2]
        : i + 4 < ps.Length && ps[i + 1] == 2 && ps[(i + 2)..(i + 5)].All(c => c is >= 0 and <= 255) ? Style.Rgb + (ps[i + 2] << 16) + (ps[i + 3] << 8) + ps[i + 4]
        : null;

    /// <summary>Breaks a line into rows of at most <paramref name="width"/> columns, at spaces where it can.</summary>
    public static List<List<Span>> Wrap(List<Span> line, int width)
    {
        width = Math.Max(1, width);
        var cells = new List<(Rune Rune, Style Style, int Columns)>();
        foreach (var span in line)
            foreach (var rune in span.Text.EnumerateRunes())
                cells.Add((rune, span.Style, Math.Max(0, rune.GetColumns())));

        List<List<Span>> rows = [];
        var start = 0;
        while (start < cells.Count || rows.Count == 0)
        {
            int end = start, used = 0, lastSpace = -1;
            while (end < cells.Count && used + cells[end].Columns <= width)
            {
                if (cells[end].Rune.Value == ' ')
                    lastSpace = end;
                used += cells[end].Columns;
                end++;
            }
            if (end == start && end < cells.Count)
                end++; // a character wider than the row still has to go somewhere
            else if (end < cells.Count && lastSpace > start && !LongerThan(cells, lastSpace + 1, width))
                end = lastSpace + 1;
            rows.Add(Spans(cells, start, end));
            start = end;
            if (start >= cells.Count)
                break;
        }
        // A line that fills its rows with a background fills every one of them.
        if (line is [.., { Text.Length: 0 } fill])
            foreach (var row in rows)
                row.Add(fill);
        return rows;
    }

    // A word that can't fit on any row is broken where the row ends instead of being moved to the next one.
    static bool LongerThan(List<(Rune Rune, Style Style, int Columns)> cells, int from, int width)
    {
        var columns = 0;
        for (var i = from; i < cells.Count && cells[i].Rune.Value != ' '; i++)
            if ((columns += cells[i].Columns) > width)
                return true;
        return false;
    }

    static List<Span> Spans(List<(Rune Rune, Style Style, int Columns)> cells, int start, int end)
    {
        List<Span> spans = [];
        var text = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            if (i > start && cells[i].Style != cells[i - 1].Style)
            {
                spans.Add(new Span(text.ToString(), cells[i - 1].Style));
                text.Clear();
            }
            text.Append(cells[i].Rune.ToString());
        }
        if (text.Length > 0)
            spans.Add(new Span(text.ToString(), cells[end - 1].Style));
        return spans;
    }
}
