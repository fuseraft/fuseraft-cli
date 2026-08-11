using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace fuseraft.Cli.Display;

/// <summary>
/// Converts a markdown string to a Spectre.Console IRenderable using deterministic rules.
/// Handles bold, italic, code spans, headings, tables, blockquotes, lists, and fenced code blocks.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Regex HeadingPattern = new(@"^(#{1,6})\s+(.+)$",     RegexOptions.Compiled);
    private static readonly Regex HrPattern      = new(@"^(\*{3,}|-{3,}|_{3,})$", RegexOptions.Compiled);
    private static readonly Regex ListPattern    = new(@"^(\s*)[-*+]\s+(.+)$",    RegexOptions.Compiled);
    private static readonly Regex OListPattern   = new(@"^(\s*)\d+[.)]\s+(.+)$",  RegexOptions.Compiled);

    public static IRenderable Render(string markdown)
    {
        var blocks = ParseBlocks(markdown.Trim());
        if (blocks.Count == 0) return new Markup(Markup.Escape(markdown));
        if (blocks.Count == 1) return blocks[0];
        return new Rows(blocks);
    }

    // -------------------------------------------------------------------------
    // Block parser
    // -------------------------------------------------------------------------

    private static List<IRenderable> ParseBlocks(string text)
    {
        var blocks = new List<IRenderable>();
        var lines  = text.ReplaceLineEndings("\n").Split('\n');
        var i      = 0;

        while (i < lines.Length)
        {
            var line    = lines[i].TrimEnd();
            var trimmed = line.TrimStart();

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            // Fenced code block
            if (trimmed.StartsWith("```"))
            {
                var lang = trimmed[3..].Trim();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    code.AppendLine(lines[i].TrimEnd());
                    i++;
                }
                i++; // skip closing ```
                var codeStr = code.ToString().TrimEnd();
                var panel = new Panel(new Text(codeStr))
                {
                    Border      = BoxBorder.Rounded,
                    BorderStyle = Style.Parse("dim"),
                    Padding     = new Padding(1, 0),
                    Expand      = false,
                };
                if (lang.Length > 0)
                    panel.Header = new PanelHeader($"[dim]{Markup.Escape(lang)}[/]", Justify.Left);
                blocks.Add(panel);
                continue;
            }

            // Table: line starts with |
            if (trimmed.StartsWith("|"))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                {
                    tableLines.Add(lines[i]);
                    i++;
                }
                var tbl = BuildTable(tableLines);
                if (tbl is not null) blocks.Add(tbl);
                continue;
            }

            // Horizontal rule
            if (HrPattern.IsMatch(trimmed))
            {
                blocks.Add(new Rule().RuleStyle("dim"));
                i++;
                continue;
            }

            // Heading
            var hm = HeadingPattern.Match(line);
            if (hm.Success)
            {
                if (blocks.Count > 0) blocks.Add(new Markup(""));
                var level  = hm.Groups[1].Value.Length;
                var hText  = hm.Groups[2].Value;
                var markup = level == 1
                    ? $"[bold underline]{ConvertInline(hText)}[/]"
                    : $"[bold]{ConvertInline(hText)}[/]";
                blocks.Add(new Markup(markup));
                i++;
                continue;
            }

            // Blockquote
            if (trimmed.StartsWith(">"))
            {
                var sb = new StringBuilder();
                while (i < lines.Length)
                {
                    var bqLine = lines[i].TrimStart();
                    if (!bqLine.StartsWith(">")) break;
                    var content = bqLine.TrimStart('>').TrimStart();
                    sb.AppendLine($"[dim]  ▏ {ConvertInline(content)}[/]");
                    i++;
                }
                blocks.Add(new Markup(sb.ToString().TrimEnd()));
                continue;
            }

            // Unordered list item
            var lm = ListPattern.Match(line);
            if (lm.Success)
            {
                var indentLen = lm.Groups[1].Value.Length;
                var content   = new StringBuilder(lm.Groups[2].Value);
                var prefix    = indentLen > 0 ? new string(' ', indentLen) : "";
                i++;
                while (i < lines.Length && !IsBlockBoundary(lines[i]))
                {
                    content.Append(' ').Append(lines[i].Trim());
                    i++;
                }
                blocks.Add(new Markup($"{prefix}[dim]•[/] {ConvertInline(content.ToString())}"));
                continue;
            }

            // Ordered list item
            var om = OListPattern.Match(line);
            if (om.Success)
            {
                var indentLen = om.Groups[1].Value.Length;
                var content   = new StringBuilder(om.Groups[2].Value);
                var numMatch  = Regex.Match(line, @"^\s*(\d+)");
                var num       = numMatch.Success ? numMatch.Groups[1].Value : "1";
                var prefix    = indentLen > 0 ? new string(' ', indentLen) : "";
                i++;
                while (i < lines.Length && !IsBlockBoundary(lines[i]))
                {
                    content.Append(' ').Append(lines[i].Trim());
                    i++;
                }
                blocks.Add(new Markup($"{prefix}[dim]{Markup.Escape(num)}.[/] {ConvertInline(content.ToString())}"));
                continue;
            }

            // Paragraph: accumulate contiguous non-structural lines, reflowed as one
            // logical line so Spectre.Console can wrap it to the actual console width.
            var para = new StringBuilder();
            while (i < lines.Length && !IsBlockBoundary(lines[i]))
            {
                if (para.Length > 0) para.Append(' ');
                para.Append(lines[i].Trim());
                i++;
            }

            if (para.Length > 0)
                blocks.Add(new Markup(ConvertInline(para.ToString())));
        }

        return blocks;
    }

    // True if a line starts a new block (or is blank) and therefore cannot be a
    // soft-wrapped continuation of the paragraph/list item being accumulated.
    private static bool IsBlockBoundary(string line)
    {
        var trimmed = line.TrimStart();
        return string.IsNullOrWhiteSpace(line)
            || trimmed.StartsWith("```")
            || trimmed.StartsWith("|")
            || HeadingPattern.IsMatch(line)
            || trimmed.StartsWith(">")
            || ListPattern.IsMatch(line)
            || OListPattern.IsMatch(line)
            || HrPattern.IsMatch(trimmed);
    }

    // -------------------------------------------------------------------------
    // Table builder
    // -------------------------------------------------------------------------

    private static Table? BuildTable(List<string> lines)
    {
        if (lines.Count == 0) return null;
        var headers = SplitRow(lines[0]);
        if (headers.Length == 0) return null;

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        foreach (var h in headers)
            table.AddColumn(new TableColumn(new Markup($"[bold]{ConvertInline(h.Trim())}[/]")));

        var dataStart = 1;
        if (lines.Count > 1 && IsSeparatorRow(lines[1]))
            dataStart = 2;

        for (var i = dataStart; i < lines.Count; i++)
        {
            var cells = SplitRow(lines[i]);
            var row   = Enumerable.Range(0, headers.Length)
                .Select(j => j < cells.Length ? ConvertInline(cells[j].Trim()) : "")
                .ToArray();
            table.AddRow(row);
        }

        return table;
    }

    private static bool IsSeparatorRow(string line) =>
        Regex.IsMatch(line.Trim(), @"^\|?[\s:\-]+(\|[\s:\-]+)*\|?$");

    private static string[] SplitRow(string line)
    {
        var s = line.Trim().Trim('|');
        return string.IsNullOrWhiteSpace(s) ? [] : s.Split('|');
    }

    // -------------------------------------------------------------------------
    // Inline markdown converter
    // -------------------------------------------------------------------------

    public static string ConvertInline(string text)
    {
        var result = new StringBuilder();
        var pos    = 0;
        var len    = text.Length;

        while (pos < len)
        {
            var (markerPos, marker) = FindNextMarker(text, pos);

            if (markerPos < 0)
            {
                result.Append(Markup.Escape(text[pos..]));
                break;
            }

            if (markerPos > pos)
                result.Append(Markup.Escape(text[pos..markerPos]));

            var searchFrom = markerPos + marker.Length;
            var closePos   = text.IndexOf(marker, searchFrom, StringComparison.Ordinal);

            if (closePos < 0 || closePos == markerPos + marker.Length)
            {
                result.Append(Markup.Escape(marker));
                pos = searchFrom;
                continue;
            }

            var inner = text[searchFrom..closePos];

            if (inner.Contains('\n'))
            {
                result.Append(Markup.Escape(marker));
                pos = searchFrom;
                continue;
            }

            result.Append(marker switch
            {
                "***" or "___" => $"[bold italic]{Markup.Escape(inner)}[/]",
                "**"  or "__"  => $"[bold]{Markup.Escape(inner)}[/]",
                "*"            => $"[italic]{Markup.Escape(inner)}[/]",
                "`"            => $"[dim]{Markup.Escape(inner)}[/]",
                _              => Markup.Escape(marker + inner + marker),
            });

            pos = closePos + marker.Length;
        }

        return result.ToString();
    }

    private static (int pos, string marker) FindNextMarker(string text, int start)
    {
        var best    = -1;
        var bestMkr = string.Empty;

        // Longer markers must be checked before shorter ones to prefer greedy match.
        foreach (var mkr in (string[])["***", "___", "**", "__", "*", "`"])
        {
            var idx = text.IndexOf(mkr, start, StringComparison.Ordinal);
            if (idx >= 0 && (best < 0 || idx < best))
            {
                best    = idx;
                bestMkr = mkr;
            }
        }

        return (best, bestMkr);
    }
}
