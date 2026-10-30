using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace fuseraft.Cli.Tui;

/// <summary>Draws the Renderer's ANSI-styled text in a view, for the lines outside the transcript.</summary>
static class Styled
{
    /// <summary>Plain text on the screen: the theme's text color and background, or the terminal's own.</summary>
    public static Attribute Plain => Of(Theme.StyleOf(Theme.Current.Text), new(Color.None, Color.None));

    public static Attribute Accent => Of(Theme.StyleOf(Theme.Current.Accent) with { Bold = true }, Plain);

    public static Attribute Border => Of(Theme.StyleOf(Theme.Current.Border), Plain);

    /// <summary>The chosen suggestion: the theme's selection on top of the accent.</summary>
    public static Attribute Selected => Theme.Current.Selection.Length == 0 ? Accent : Of(Theme.StyleOf(Theme.Current.Selection), Accent);

    /// <summary>A search match in text drawn in <paramref name="attribute"/>: the theme's selection on top, or reversed.</summary>
    public static Attribute Marked(Attribute attribute) => Theme.Current.Selection.Length == 0
        ? attribute with { Style = attribute.Style | TextStyle.Reverse } : Of(Theme.StyleOf(Theme.Current.Selection), attribute);

    public static List<Span> Parse(string ansi)
    {
        var transcript = new Transcript();
        transcript.Write(ansi);
        return transcript.Lines()[0];
    }

    /// <summary>One row of styled text, cut to the view's width with "…"; unstyled text is drawn in <paramref name="plain"/>.</summary>
    public static void Draw(View view, int row, List<Span> spans, Attribute plain)
    {
        var width = view.Viewport.Width;
        var overflow = spans.Sum(s => s.Text.GetColumns()) > width;
        var room = overflow ? width - 1 : width;
        var used = 0;
        view.Move(0, row);
        foreach (var span in spans)
        {
            var fits = new System.Text.StringBuilder();
            foreach (var rune in span.Text.EnumerateRunes())
            {
                var columns = Math.Max(0, rune.GetColumns());
                if (used + columns > room)
                    break;
                fits.Append(rune.ToString());
                used += columns;
            }
            view.SetAttribute(Of(span.Style, plain));
            view.AddStr(fits.ToString());
            if (used >= room)
                break;
        }
        view.SetAttribute(plain);
        if (overflow && width > 0)
        {
            view.AddStr("…");
            used++;
        }
        view.AddStr(new string(' ', Math.Max(0, width - used)));
    }

    /// <summary>A style as an attribute; the colors it leaves unset are <paramref name="plain"/>'s.</summary>
    public static Attribute Of(Style style, Attribute plain)
    {
        if (style == default)
            return plain;
        var text = (style.Bold ? TextStyle.Bold : TextStyle.None) | (style.Dim ? TextStyle.Faint : TextStyle.None)
            | (style.Italic ? TextStyle.Italic : TextStyle.None) | (style.Underline ? TextStyle.Underline : TextStyle.None);
        return new Attribute(style.Color == 0 ? plain.Foreground : ColorOf(style.Color), style.Background == 0 ? plain.Background : ColorOf(style.Background), text);
    }

    static Color ColorOf(int code) =>
        // 256-colors 0-15 are the terminal's own 16, so they go through the same names.
        (code is >= 256 and < 272 ? code - 256 + (code < 264 ? 30 : 82) : code) switch
        {
            30 => new Color(ColorName16.Black),
            31 => new Color(ColorName16.Red),
            32 => new Color(ColorName16.Green),
            33 => new Color(ColorName16.Yellow),
            34 => new Color(ColorName16.Blue),
            35 => new Color(ColorName16.Magenta),
            36 => new Color(ColorName16.Cyan),
            37 => new Color(ColorName16.Gray),
            90 => new Color(ColorName16.DarkGray),
            91 => new Color(ColorName16.BrightRed),
            92 => new Color(ColorName16.BrightGreen),
            93 => new Color(ColorName16.BrightYellow),
            94 => new Color(ColorName16.BrightBlue),
            95 => new Color(ColorName16.BrightMagenta),
            96 => new Color(ColorName16.BrightCyan),
            97 => new Color(ColorName16.White),
            >= 256 and < 512 => Rgb(Theme.Rgb(code - 256)),
            >= Style.Rgb => Rgb(((code >> 16) & 255, (code >> 8) & 255, code & 255)),
            _ => Color.None,
        };

    static Color Rgb((int R, int G, int B) c) => new(c.R, c.G, c.B);
}

/// <summary>Takes the prompt's place for approvals, questions and setup: a few lines of text, and a key handler.</summary>
sealed class PanelView : View
{
    List<(string Text, bool Highlighted)> _content = [];
    DateTime _quietFrom;
    TimeSpan _quiet;

    public PanelView() => CanFocus = true;

    public Func<Key, bool>? OnKey { get; set; }

    public int Rows { get; set; }

    public int Needed() => Math.Max(Rows, _content.Count);

    public void Show(List<(string Text, bool Highlighted)> content)
    {
        _content = content;
        SetNeedsDraw();
        SuperView?.SetNeedsLayout();
    }

    public void Reset(DateTime lastTyped, TimeSpan quiet)
    {
        _quietFrom = lastTyped;
        _quiet = quiet;
        _content = [];
        OnKey = null;
        foreach (var sub in SubViews.ToList())
        {
            Remove(sub);
            sub.Dispose();
        }
    }

    // Ctrl+C: answers as Esc would, even while keys are still being ignored.
    public void Dismiss() => OnKey?.Invoke(Key.Esc);

    protected override bool OnKeyDown(Key key)
    {
        // Keys still coming from typing into the prompt are swallowed, and keep the window open, until the typing stops.
        if (DateTime.UtcNow - _quietFrom < _quiet)
        {
            _quietFrom = DateTime.UtcNow;
            return true;
        }
        return OnKey?.Invoke(key) ?? false;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        for (var i = 0; i < Viewport.Height; i++)
        {
            var (text, highlighted) = i < _content.Count ? _content[i] : ("", false);
            Styled.Draw(this, i, Styled.Parse(text), highlighted ? Styled.Accent : Styled.Plain);
        }
        return true;
    }
}

/// <summary>The line above the prompt; says how much is below when the transcript is scrolled up, or what the viewer shows.</summary>
sealed class RuleView : View
{
    int _below;
    string? _note;

    /// <summary>Drawn in place of the scroll count while set.</summary>
    public string? Note
    {
        get => _note;
        set
        {
            _note = value;
            SetNeedsDraw();
        }
    }

    public int Below
    {
        get => _below;
        set
        {
            _below = value;
            SetNeedsDraw();
        }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var label = _note ?? (_below > 0 ? $" ↓ {_below} more line{(_below == 1 ? "" : "s")} · Ctrl+End to follow " : "");
        Styled.Draw(this, 0, [new("──" + label + new string('─', Math.Max(0, Viewport.Width - 2 - label.GetColumns())), default)], Styled.Border);
        return true;
    }
}

/// <summary>The bottom line: a spinner while working, the REPL's status, and the keys that matter.</summary>
sealed class StatusView : View
{
    static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    string _text = "";
    string? _flash;
    int _frame;
    DateTime _since; // when the running turn started
    object? _timer;

    public bool Working => _working;

    volatile bool _working;
    volatile bool _asking;

    /// <summary>A question in the panel is waiting on the user, so nothing is working until it's answered.</summary>
    public bool Asking
    {
        get => _asking;
        set
        {
            _asking = value;
            SetNeedsDraw();
        }
    }

    /// <summary>The session is ending, and takes no more input.</summary>
    public bool Ending { get; set; }

    /// <summary>The keys shown at the right: none while a question is asked, since the panel shows its own, or as the session ends.</summary>
    public static string Keys(bool working, bool asking, bool ending = false) =>
        asking || ending ? "" : working ? "Enter adds to the turn · Ctrl+C cancel" : "Enter send · Alt+Enter newline · PgUp/PgDn scroll · Ctrl+O output";

    public void Set(string text, bool working)
    {
        if (working && !_working)
            _since = DateTime.UtcNow;
        _text = text;
        _working = working;
        _flash = null;
        if (working && _timer is null && App is { } app)
            _timer = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                _frame++;
                SetNeedsDraw();
                if (_working)
                    return true;
                _timer = null;
                return false;
            });
        SetNeedsDraw();
    }

    /// <summary>How long a turn has run, as the status line shows it: 9s, 1m05s, 1h02m.</summary>
    public static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s" : $"{(int)t.TotalSeconds}s";

    /// <summary>Shows a hint until the next key, or until <paramref name="expires"/> passes.</summary>
    public void Flash(string message, TimeSpan? expires = null)
    {
        _flash = message;
        SetNeedsDraw();
        if (expires is { } after && App is { } app)
            app.AddTimeout(after, () =>
            {
                if (ReferenceEquals(_flash, message))
                    ClearFlash();
                return false;
            });
    }

    public void ClearFlash()
    {
        if (_flash is null)
            return;
        _flash = null;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        List<Span> left = _flash is not null ? [new(_flash, Theme.StyleOf(Theme.Current.Warning))]
            : _working && !_asking ? [new(Spinner[_frame % Spinner.Length], Theme.StyleOf(Theme.Current.Accent) with { Bold = true }), new($" working {Elapsed(DateTime.UtcNow - _since)} · ", default), .. Styled.Parse(_text)]
            : Styled.Parse(_text);
        var right = Keys(_working, _asking, Ending);
        var gap = Viewport.Width - left.Sum(s => s.Text.GetColumns()) - right.GetColumns();
        Styled.Draw(this, 0, gap >= 2 ? [.. left, new(new string(' ', gap) + right, default)] : left, Styled.Border);
        return true;
    }
}

/// <summary>The prompt's suggestions, above it, the chosen one picked out.</summary>
internal sealed class SuggestView : View
{
    IReadOnlyList<Suggestion> _items = [];
    int _selected;
    int _top;

    public void Show(IReadOnlyList<Suggestion> items, int selected)
    {
        if (!ReferenceEquals(items, _items))
            _top = 0;
        (_items, _selected) = (items, selected);
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = _items.Count == 0 ? 0 : _items.Max(s => s.Text.GetColumns());
        var muted = Theme.StyleOf(Theme.Current.Muted);
        if (Viewport.Height <= 0)
            return true;
        _top = Math.Clamp(_top, Math.Max(0, _selected - Viewport.Height + 1), _selected);
        for (var row = 0; row < Viewport.Height; row++)
        {
            var i = _top + row;
            if (i >= _items.Count)
            {
                Styled.Draw(this, row, [], Styled.Plain);
                continue;
            }
            var (item, chosen) = (_items[i], i == _selected);
            var note = item.Note.Length == 0 ? "" : new string(' ', width - item.Text.GetColumns() + 3) + item.Note;
            Styled.Draw(this, row, [new((chosen ? "› " : "  ") + item.Text, chosen ? default : muted), new(note, muted),
                new(chosen ? "   Tab to complete" : "", muted)], chosen ? Styled.Selected : Styled.Plain);
        }
        return true;
    }
}

/// <summary>What was sent during a turn to run after it, above the prompt, oldest first.</summary>
internal sealed class QueueView : View
{
    public const int MaxShown = 3;

    IReadOnlyList<string> _items = [];

    /// <summary>Rows it needs: one per message, up to <see cref="MaxShown"/>.</summary>
    public int Rows => Math.Min(_items.Count, MaxShown);

    public void Show(IReadOnlyList<string> items)
    {
        _items = items;
        SetNeedsDraw();
    }

    /// <summary>Row <paramref name="row"/>'s text: a message on one line, or how many more there are on the last row.</summary>
    public static string Row(IReadOnlyList<string> items, int row) =>
        row == MaxShown - 1 && items.Count > MaxShown ? $"  queued · {items.Count - row} more"
        : "  queued › " + items[row].ReplaceLineEndings(" ⏎ ");

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var muted = Theme.StyleOf(Theme.Current.Muted);
        const string hint = "↑ to edit the last";
        for (var row = 0; row < Viewport.Height; row++)
        {
            if (row >= Rows)
            {
                Styled.Draw(this, row, [], Styled.Plain);
                continue;
            }
            var text = Row(_items, row);
            var gap = Viewport.Width - text.GetColumns() - hint.GetColumns();
            Styled.Draw(this, row, row == 0 && gap >= 2 ? [new(text, muted), new(new string(' ', gap) + hint, muted)] : [new(text, muted)], Styled.Plain);
        }
        return true;
    }
}
