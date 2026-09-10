using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace fuseraft.Cli.Display;

/// <summary>
/// Renders a git-diff-style unified view of an old/new text pair as a Spectre.Console
/// IRenderable. Used by the HITL write_file/patch_file approval prompt (see
/// <see cref="fuseraft.Cli.ConsoleHumanApprovalService.PromptFileWriteAsync"/>) so the user
/// sees the actual content change instead of just a bare file path before approving.
/// </summary>
public static class DiffRenderer
{
    // How many unchanged lines to keep on either side of a change before collapsing
    // the rest of a long unchanged run — matches `git diff`'s default -U3 context.
    private const int ContextLines = 3;

    // Approval prompts render inline in the console before a blocking y/N read, so a huge
    // diff (a large generated file) is capped rather than flooding the terminal.
    private const int MaxDisplayLines = 400;

    private const int MaxLineChars = 300;

    private enum RowKind { Context, Added, Removed, Collapsed }

    private readonly record struct Row(RowKind Kind, int? OldLine, int? NewLine, string Text);

    public static IRenderable Render(string path, string oldContent, string newContent)
    {
        // Trim a single trailing newline from each side before diffing — otherwise the text
        // after the file's final "\n" becomes a phantom trailing "line" (empty string) that
        // is technically unchanged but has nothing meaningful to show, producing a confusing
        // "N unchanged lines" collapse marker after content that's already fully displayed.
        var normalizedOld = oldContent.ReplaceLineEndings("\n");
        var normalizedNew = newContent.ReplaceLineEndings("\n");
        if (normalizedOld.EndsWith('\n')) normalizedOld = normalizedOld[..^1];
        if (normalizedNew.EndsWith('\n')) normalizedNew = normalizedNew[..^1];

        if (normalizedOld == normalizedNew)
            return new Markup($"[dim]{Markup.Escape(path)} — content unchanged.[/]");

        var model = new InlineDiffBuilder(new Differ()).BuildDiffModel(normalizedOld, normalizedNew);

        var rows    = new List<Row>();
        var oldNo   = 0;
        var newNo   = 0;
        var added   = 0;
        var removed = 0;

        foreach (var line in model.Lines)
        {
            switch (line.Type)
            {
                case ChangeType.Deleted:
                    oldNo++;
                    removed++;
                    rows.Add(new Row(RowKind.Removed, oldNo, null, line.Text));
                    break;
                case ChangeType.Inserted:
                    newNo++;
                    added++;
                    rows.Add(new Row(RowKind.Added, null, newNo, line.Text));
                    break;
                default: // Unchanged — Imaginary never appears in InlineDiffBuilder output
                    oldNo++;
                    newNo++;
                    rows.Add(new Row(RowKind.Context, oldNo, newNo, line.Text));
                    break;
            }
        }

        var collapsed   = CollapseContext(rows);
        var displayRows = collapsed;
        var truncatedBy = 0;
        if (displayRows.Count > MaxDisplayLines)
        {
            truncatedBy = displayRows.Count - MaxDisplayLines;
            displayRows = displayRows.GetRange(0, MaxDisplayLines);
        }

        var body = new StringBuilder();
        foreach (var row in displayRows)
            body.AppendLine(FormatRow(row));
        if (truncatedBy > 0)
            body.Append($"[dim]… diff truncated — {truncatedBy} more line(s) not shown[/]");

        var stat = $"[{ThemeDetector.DiffAdd}]+{added}[/] [{ThemeDetector.DiffRemove}]-{removed}[/]";
        return new Panel(new Markup(body.ToString().TrimEnd()))
        {
            Header      = new PanelHeader($"[bold]{Markup.Escape(path)}[/] {stat}", Justify.Left),
            Border      = BoxBorder.Rounded,
            BorderStyle = Style.Parse("dim"),
            Padding     = new Padding(1, 0),
            Expand      = false,
        };
    }

    // Marks every row within ContextLines of a change as "keep", then collapses each
    // remaining run of unmarked context rows into a single summary row. A file with no
    // changes at all (only reachable if the caller didn't already short-circuit on
    // identical content) collapses to one row spanning the whole file.
    private static List<Row> CollapseContext(List<Row> rows)
    {
        var n    = rows.Count;
        var keep = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (rows[i].Kind is not (RowKind.Added or RowKind.Removed)) continue;
            for (var j = Math.Max(0, i - ContextLines); j <= Math.Min(n - 1, i + ContextLines); j++)
                keep[j] = true;
        }

        var result = new List<Row>();
        var idx    = 0;
        while (idx < n)
        {
            if (keep[idx])
            {
                result.Add(rows[idx]);
                idx++;
                continue;
            }

            var start = idx;
            while (idx < n && !keep[idx]) idx++;
            var hidden = idx - start;
            result.Add(new Row(RowKind.Collapsed, null, null,
                $"{hidden} unchanged line{(hidden == 1 ? "" : "s")}"));
        }
        return result;
    }

    private static string FormatRow(Row row) => row.Kind switch
    {
        RowKind.Added     => $"[{ThemeDetector.DiffAdd}]{Gutter(null, row.NewLine)} + {EscapeLine(row.Text)}[/]",
        RowKind.Removed   => $"[{ThemeDetector.DiffRemove}]{Gutter(row.OldLine, null)} - {EscapeLine(row.Text)}[/]",
        RowKind.Context   => $"[dim]{Gutter(row.OldLine, row.NewLine)}   {EscapeLine(row.Text)}[/]",
        RowKind.Collapsed => $"[dim]     ⋮  {Markup.Escape(row.Text)}[/]",
        _                 => Markup.Escape(row.Text),
    };

    private static string Gutter(int? oldLine, int? newLine) =>
        $"{oldLine?.ToString() ?? "",4} {newLine?.ToString() ?? "",4}";

    private static string EscapeLine(string text) =>
        Markup.Escape(text.Length > MaxLineChars ? text[..MaxLineChars] + "…" : text);
}
