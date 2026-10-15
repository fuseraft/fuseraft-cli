using fuseraft.Cli.Tui;
using Terminal.Gui.Text;

namespace FuseraftCli.Tests;

public sealed class MarkdownTests
{
    static readonly Style Bold = new(0, true, false);
    static readonly Style Code = new(36, false, false);
    static readonly Style Italic = new(0, false, false, Italic: true);

    [Fact]
    public void Inline_StylesBoldCodeAndItalicAndDropsTheMarkers()
    {
        var line = Lines("Run **all** the `dotnet test` *now*")[0];

        Assert.Equal(
            [new Span("Run ", default), new Span("all", Bold), new Span(" the ", default), new Span("dotnet test", Code),
             new Span(" ", default), new Span("now", Italic)],
            line);
    }

    [Fact]
    public void Inline_LeavesCodeSpansAndIdentifiersAlone()
    {
        Assert.Equal("a*b*c and snake_case_name", Text(Lines("`a*b*c` and snake_case_name")[0]));
        Assert.Equal("2 * 3 * 4", Text(Lines("2 * 3 * 4")[0]));
        Assert.Equal("*literal*", Text(Lines(@"\*literal\*")[0]));
    }

    [Fact]
    public void Inline_NestsStyles()
    {
        var line = Lines("**see `Agent.cs`**")[0];

        Assert.Equal([new Span("see ", Bold), new Span("Agent.cs", Code with { Bold = true })], line);
    }

    [Fact]
    public void Links_ShowTheirTextAndThenTheUrl()
    {
        Assert.Equal("the docs (https://fuseraft.ai/anchor)", Text(Lines("[the docs](https://fuseraft.ai/anchor)")[0]));
        Assert.Equal("https://x.dev", Text(Lines("[https://x.dev](https://x.dev)")[0]));
    }

    [Fact]
    public void Blocks_HeadingsListsQuotesAndRules()
    {
        var lines = Lines("# Title\n## Part\n- one\n  * two\n1. first\n> quoted\n---");

        Assert.Equal(["Title", "Part", "• one", "  • two", "1. first", "│ quoted", new string('─', 40)], lines.Select(Text));
        Assert.True(lines[0][0].Style is { Bold: true, Underline: true });
        Assert.Equal(Bold, lines[1][0].Style);
    }

    [Fact]
    public void CodeBlocks_DropTheFencesAndKeepTheirContentAsWritten()
    {
        var lines = Lines("```csharp\nvar x = a**b;\n```\nafter **this**");

        Assert.Equal(["  csharp", "  var x = a**b;", "after this"], lines.Select(Text));
    }

    [Fact]
    public void Tables_LineUpTheirColumns()
    {
        var lines = Lines("| Name | Size |\n|---|--:|\n| `a.cs` | 12 |\n| longer.cs | 3 |");

        Assert.Equal(
            ["Name      │ Size", "──────────┼─────", "a.cs      │   12", "longer.cs │    3"],
            lines.Select(Text));
        Assert.Equal(Bold, lines[0][0].Style);
    }

    [Fact]
    public void CodeBlocks_InAKnownLanguageAreHighlighted()
    {
        var lines = Lines("```python\ndef f():  # note\n    return \"hi\"\n```");

        Assert.Equal(["  python", "  def f():  # note", "      return \"hi\""], lines.Select(Text));
        Assert.Contains(new Span("def", new Style(35, false, false)), lines[1]);
        Assert.Contains(new Span("f", new Style(94, false, false)), lines[1]);
        Assert.Contains(new Span("# note", new Style(0, false, true)), lines[1]);
        Assert.Contains(lines[2], s => s.Text == "\"hi\"" && s.Style.Color == 32);
    }

    [Fact]
    public void CodeBlocks_LeaveVariablesPlain()
    {
        var line = Lines("```csharp\nvoid Run(int count) { var message = count; }\n```")[1];

        Assert.All(line.Where(s => s.Text.Contains("count")), s => Assert.Equal(default, s.Style));
    }

    [Fact]
    public void CodeBlocks_CarryAStringAcrossLines()
    {
        var lines = Lines("```py\nx = \"\"\"one\ntwo\"\"\"\n```");

        Assert.Equal(32, Assert.Single(lines[2], s => s.Text.Contains("two")).Style.Color);
    }

    [Fact]
    public void CodeBlocks_InAnUnknownLanguageAreOneColor()
    {
        var lines = Lines("```nosuchlang\nlet x\tbe 1\n```");

        Assert.Equal([new Span("  ", default), new Span("let x    be 1", Code)], lines[1]);
    }

    [Fact]
    public void TaskLists_ShowTheirBoxes()
    {
        Assert.Equal(["☐ todo", "  ☑ done"], Lines("- [ ] todo\n  - [x] done").Select(Text));
    }

    [Fact]
    public void Quotes_NestAndHoldOtherBlocks()
    {
        var lines = Lines("> > deep\n> - item\n> # Title");

        Assert.Equal(["│ │ deep", "│ • item", "│ Title"], lines.Select(Text));
        Assert.True(lines[2][1].Style is { Dim: true, Bold: true });
    }

    [Fact]
    public void Images_AndAutolinks_ReadAsLinks()
    {
        Assert.Equal("diagram (d.png)", Text(Lines("![diagram](d.png)")[0]));
        Assert.Equal("see https://x.dev", Text(Lines("see <https://x.dev>")[0]));
    }

    [Fact]
    public void Tables_WiderThanTheScreen_WrapTheirWidestColumn()
    {
        var lines = Parse(Markdown.Render("| Key | Meaning |\n|---|---|\n| a | the first letter of the alphabet |", width: 26));

        Assert.Equal(
            ["Key │ Meaning", "────┼─────────────────────", "a   │ the first letter of", "    │ the alphabet"],
            lines.Select(Text));
        Assert.All(lines, l => Assert.True(Text(l).GetColumns() <= 26));
    }

    [Fact]
    public void Tables_BreakCellsAtBr()
    {
        var lines = Lines("| a | b |\n|---|---|\n| one<br>two | x |");

        Assert.Equal(["a   │ b", "────┼──", "one │ x", "two │ "], lines.Select(Text));
    }

    [Fact]
    public void Add_SettlesWholeLinesAndKeepsTheRestOpen()
    {
        var md = new Markdown();

        Assert.Equal(("", "Hello **wor"), md.Add("Hello **wor"));
        var (settled, open) = md.Add("ld**\nNext");

        Assert.Equal("Hello world", Text(Parse(settled)[0]));
        Assert.Equal("Next", open);
    }

    [Fact]
    public void Add_HoldsATableBackUntilItEnds()
    {
        var md = new Markdown();
        var (settled, open) = md.Add("| a | b |\n|---|---|\n| 1 | 2");

        Assert.Equal("", settled);
        Assert.Equal(["a │ b", "──┼──", "1 │ 2"], Parse(open).Select(Text));

        (settled, open) = md.Add(" |\n\nDone");

        Assert.Equal(["a │ b", "──┼──", "1 │ 2", ""], Parse(settled).Select(Text).SkipLast(1));
        Assert.Equal("Done", open);
    }

    [Fact]
    public void Add_RemembersACodeBlockAcrossChunks()
    {
        var md = new Markdown();
        md.Add("```\n");
        var (settled, _) = md.Add("# not a heading\n");

        Assert.Equal("  # not a heading", Text(Parse(settled)[0]));
    }

    static List<List<Span>> Lines(string markdown) => Parse(Markdown.Render(markdown));

    static List<List<Span>> Parse(string ansi)
    {
        var t = new Transcript();
        t.Write(ansi);
        return t.Lines();
    }

    static string Text(List<Span> spans) => string.Concat(spans.Select(s => s.Text));

    [Fact]
    public void CodeBlocks_FindsEachFencedBlock_WithoutItsFencesOrIndent()
    {
        var blocks = Markdown.CodeBlocks("Run:\n```sh\nmake\nmake test\n```\n- then\n  ~~~\n  x = 1\n    y = 2\n  ~~~\n````md\n```\nnot closed");

        Assert.Equal([("sh", "make\nmake test"), ("", "x = 1\n  y = 2"), ("md", "```\nnot closed")], blocks);
    }

    [Fact]
    public void CodeBlocks_IsEmptyWithoutFences() => Assert.Empty(Markdown.CodeBlocks("just `inline` code"));

    [Fact]
    public void Shade_PutsACodeBlocksBackgroundUnderEveryTokenAndToTheEndOfTheRow()
    {
        var t = new Transcript();
        t.Write(Markdown.Shade("  \e[35mint\e[0m x", "48;5;236"));

        var shaded = new Style(0, false, false, Background: 256 + 236);
        Assert.Equal([new Span("  ", shaded), new Span("int", shaded with { Color = 35 }), new Span(" x", shaded), new Span("", shaded)], t.Lines()[0]);
        Assert.Equal("  x", Markdown.Shade("  x", ""));
    }
}
