using System.Text;
using System.Text.RegularExpressions;
using Terminal.Gui.Text;

namespace fuseraft.Cli.Tui;

/// <summary>
/// Turns the model's Markdown into ANSI-styled text as it streams. Lines are styled once they're complete and then never
/// change; a table is held back until it ends, since its columns can't be sized before every row is in. What's still
/// open (a table, the line being written) is styled as it stands, for the TUI to redraw as more arrives.
/// </summary>
internal sealed partial class Markdown(int width = int.MaxValue)
{
    const string Bold = "1", Dim = "2", Italic = "3", Underline = "4";

    static Theme T => Theme.Current;

    readonly StringBuilder _pending = new();
    readonly List<string> _table = [];
    string? _fence; // what opened the code block we're in: ``` or ~~~, maybe longer
    Highlighter? _code; // colors that code block, when its language is one the grammars know

    /// <summary>Styles a whole document, such as an answer from an earlier session.</summary>
    public static string Render(string text, int width = int.MaxValue)
    {
        var (settled, open) = new Markdown(width).Add(text + "\n");
        return (settled + open).TrimEnd('\n');
    }

    /// <summary>
    /// Takes the next streamed text. Returns the lines that are now final, each ending in a newline, and how the rest
    /// looks so far: any table being written, then the open line, with no newline after it.
    /// </summary>
    public (string Settled, string Open) Add(string text)
    {
        _pending.Append(text);
        var settled = new StringBuilder();
        var all = _pending.ToString();
        var start = 0;
        for (var nl = all.IndexOf('\n'); nl >= 0; start = nl + 1, nl = all.IndexOf('\n', start))
        {
            var line = all[start..nl].TrimEnd('\r');
            if (_fence is null && IsTableRow(line))
            {
                _table.Add(line);
                continue;
            }
            if (_table.Count > 0)
            {
                settled.Append(Table(_table)).Append('\n');
                _table.Clear();
            }
            if (Line(line, settle: true) is { } styled)
                settled.Append(styled).Append('\n');
        }
        _pending.Remove(0, start);

        var partial = _pending.ToString();
        List<string> rows = [.. _table];
        if (_fence is null && IsTableRow(partial))
        {
            rows.Add(partial);
            partial = "";
        }
        var open = new StringBuilder();
        if (rows.Count > 0)
            open.Append(Table(rows)).Append(partial.Length > 0 ? "\n" : "");
        if (partial.Length > 0)
            open.Append(Line(partial, settle: false));
        return (settled.ToString(), open.ToString());
    }

    // One line outside a table. Null leaves it out: the fences around a code block. A line that isn't finished yet
    // doesn't settle: it can't open or close a code block, or move the highlighter on.
    string? Line(string line, bool settle)
    {
        if (_fence is not null)
        {
            if (line.TrimStart() is var close && close.StartsWith(_fence) && close.Trim(_fence[0]).Trim().Length == 0)
            {
                if (settle)
                    (_fence, _code) = (null, null);
                return null;
            }
            line = line.Replace("\t", "    ");
            return Shade("  " + (_code is null ? Paint(T.Code, line) : _code.Line(line, settle)), T.CodeBlock);
        }
        if (FenceOpen().Match(line) is { Success: true } f)
        {
            var language = f.Groups[2].Value;
            if (settle)
                (_fence, _code) = (f.Groups[1].Value, Highlighter.For(language));
            return language.Length > 0 ? Shade(Paint(T.Muted, "  " + language), T.CodeBlock) : null;
        }
        return Block(line, "");
    }

    // Headings, rules, lists and quotes, in the style of whatever they sit in (a quote's text is dim).
    string Block(string line, string style)
    {
        if (Heading().Match(line) is { Success: true } h)
            return Inline(h.Groups[2].Value, With(style, h.Groups[1].Length == 1 ? With(T.Heading, Underline) : T.Heading));
        if (Rule().IsMatch(line))
            return Paint(With(style, T.Muted), new string('─', Math.Min(40, width)));
        if (Task().Match(line) is { Success: true } t)
            return t.Groups[1].Value + Paint(style, t.Groups[2].Value == " " ? "☐ " : "☑ ") + Inline(t.Groups[3].Value, style);
        if (Bullet().Match(line) is { Success: true } b)
            return b.Groups[1].Value + Paint(style, "• ") + Inline(b.Groups[2].Value, style);
        if (Quote().Match(line) is { Success: true } q)
            return Paint(T.Muted, "│ ") + Block(q.Groups[1].Value, With(style, T.Muted));
        return Inline(line, style);
    }

    static bool IsTableRow(string line) => line.TrimStart().StartsWith('|');

    // Columns padded to their widest cell, the header in bold, and the separator row drawn as a line. When that's wider
    // than the screen, the widest columns give way and their cells wrap.
    string Table(List<string> lines)
    {
        var rows = lines.Select(Cells).ToList();
        var divider = rows.FindIndex(r => r.Count > 0 && r.All(c => Divider().IsMatch(c)));
        var right = divider < 0 ? [] : rows[divider].Select(c => c.EndsWith(':') && !c.StartsWith(':')).ToList();
        // Each cell as its styled lines: a <br> in it breaks the line.
        var cells = rows.Select((r, i) => i == divider ? [] : r.Select(c => Break().Split(c).Select(part => Inline(part, i < divider ? Bold : "")).ToList()).ToList()).ToList();
        var columns = cells.Max(r => r.Count);
        var widths = Fit(Enumerable.Range(0, columns).Select(c => cells.Max(r => c < r.Count ? r[c].Max(Width) : 0)).ToList(), width - 3 * (columns - 1));

        var sb = new StringBuilder();
        for (var i = 0; i < cells.Count; i++)
        {
            if (i == divider)
            {
                sb.Append(Paint(T.Muted, string.Join("─┼─", widths.Select(w => new string('─', w))))).Append('\n');
                continue;
            }
            var wrapped = Enumerable.Range(0, columns).Select(c => c < cells[i].Count ? cells[i][c].SelectMany(part => Wrap(part, widths[c])).ToList() : []).ToList();
            for (var row = 0; row < Math.Max(1, wrapped.Max(w => w.Count)); row++)
            {
                for (var c = 0; c < columns; c++)
                {
                    var cell = row < wrapped[c].Count ? wrapped[c][row] : "";
                    var pad = new string(' ', Math.Max(0, widths[c] - Width(cell)));
                    if (c > 0)
                        sb.Append(Paint(T.Muted, " │ "));
                    sb.Append(c < right.Count && right[c] ? pad + cell : c == columns - 1 ? cell : cell + pad);
                }
                sb.Append('\n');
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    // Narrows the widest columns, one column of text at a time, until the table fits or no column can give more.
    static List<int> Fit(List<int> widths, int room)
    {
        const int narrowest = 8;
        while (widths.Sum() > room)
        {
            var widest = widths.IndexOf(widths.Max());
            if (widths[widest] <= narrowest)
                break;
            widths[widest]--;
        }
        return widths;
    }

    // Styled text broken into lines of at most `columns`, at spaces where it can.
    static IEnumerable<string> Wrap(string styled, int columns)
    {
        if (Width(styled) <= columns)
            return [styled];
        var spans = new Transcript();
        spans.Write(styled);
        return Transcript.Wrap(spans.Lines()[0], columns).Select(row => Transcript.Ansi(row).TrimEnd());
    }

    static List<string> Cells(string row)
    {
        row = row.Trim();
        if (row.StartsWith('|'))
            row = row[1..];
        if (row.EndsWith('|') && !row.EndsWith("\\|"))
            row = row[..^1];
        return [.. CellSplit().Split(row).Select(c => c.Trim().Replace("\\|", "|"))];
    }

    // Code spans, links, bold, italic and strikethrough, each styled on top of the text around it.
    static string Inline(string text, string style = "")
    {
        var sb = new StringBuilder();
        var at = 0;
        foreach (Match m in Span().Matches(text))
        {
            sb.Append(Paint(style, text[at..m.Index]));
            at = m.Index + m.Length;
            if (m.Groups["code"].Success)
            {
                var code = m.Groups["code"].Value;
                if (code.Length > 2 && code[0] == ' ' && code[^1] == ' ')
                    code = code[1..^1];
                sb.Append(Paint(With(style, T.Code), code));
            }
            else if (m.Groups["text"].Success)
            {
                var (label, url) = (m.Groups["text"].Value, m.Groups["url"].Value);
                sb.Append(label == url ? Paint(With(style, T.Link), url) : Inline(label, With(style, T.Link)) + Paint(T.Muted, $" ({url})"));
            }
            else if (m.Groups["auto"].Success)
                sb.Append(Paint(With(style, T.Link), m.Groups["auto"].Value));
            else if (m.Groups["bold"].Success)
                sb.Append(Inline(m.Groups["bold"].Value, With(style, Bold)));
            else if (m.Groups["italic"].Success)
                sb.Append(Inline(m.Groups["italic"].Value, With(style, Italic)));
            else if (m.Groups["strike"].Success)
                sb.Append(Inline(m.Groups["strike"].Value, With(style, Dim)));
            else
                sb.Append(Paint(style, m.Groups["escaped"].Value));
        }
        sb.Append(Paint(style, text[at..]));
        return sb.ToString();
    }

    static string With(string style, string code) => style.Length == 0 ? code : code.Length == 0 ? style : style + ";" + code;

    /// <summary>
    /// A line of a code block on the block's background: under every token, each of which ends in a reset, and to the end
    /// of the row.
    /// </summary>
    internal static string Shade(string line, string background) =>
        background.Length == 0 ? line : $"\e[{background}m" + line.Replace("\e[0m", $"\e[0;{background}m") + "\e[K\e[0m";

    static string Paint(string style, string s) => style.Length == 0 || s.Length == 0 ? s : $"\e[{style}m{s}\e[0m";

    static int Width(string styled) => Ansi().Replace(styled, "").GetColumns();

    /// <summary>
    /// The fenced code blocks in <paramref name="markdown"/>, by the same rules the renderer uses, without their
    /// fences and with the fence's indentation taken off each line. A block the text ends inside runs to the end.
    /// </summary>
    public static List<(string Language, string Code)> CodeBlocks(string markdown)
    {
        List<(string, string)> blocks = [];
        string? fence = null, language = null;
        var indent = 0;
        List<string> code = [];
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (fence is null)
            {
                if (FenceOpen().Match(line) is { Success: true } f)
                    (fence, language, indent, code) = (f.Groups[1].Value, f.Groups[2].Value, line.Length - line.TrimStart().Length, []);
            }
            else if (line.TrimStart() is var close && close.StartsWith(fence) && close.Trim(fence[0]).Trim().Length == 0)
            {
                blocks.Add((language!, string.Join('\n', code)));
                fence = null;
            }
            else
                code.Add(line[Math.Min(indent, line.Length - line.TrimStart().Length)..]);
        }
        if (fence is not null)
            blocks.Add((language!, string.Join('\n', code)));
        return blocks;
    }

    [GeneratedRegex(@"^\s*(`{3,}|~{3,})\s*([^`\s]*)")]
    private static partial Regex FenceOpen();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)(\s+#+)?\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^(\s*)[-*+]\s+\[([ xX])\]\s+(.*)$")]
    private static partial Regex Task();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^\s{0,3}>\s?(.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^:?-+:?$")]
    private static partial Regex Divider();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex CellSplit();

    [GeneratedRegex(@"\s*<br\s*/?>\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Break();

    [GeneratedRegex(@"\e\[[0-9;]*[mK]")]
    private static partial Regex Ansi();

    [GeneratedRegex("""
        (?<ticks>`+)(?<code>.+?)\k<ticks>
        | !?\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)\)
        | <(?<auto>https?://[^>\s]+)>
        | \*\*(?<bold>\S(?:.*?\S)?)\*\*
        | (?<!\w)__(?<bold>\S(?:.*?\S)?)__(?!\w)
        | ~~(?<strike>\S(?:.*?\S)?)~~
        | \*(?<italic>[^\s*](?:.*?[^\s*])?)\*
        | (?<!\w)_(?<italic>[^\s_](?:.*?[^\s_])?)_(?!\w)
        | \\(?<escaped>[\\`*_{}\[\]()#+\-.!|~>])
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Span();
}
