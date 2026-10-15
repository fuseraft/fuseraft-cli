using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class TranscriptTests
{
    static readonly Style Plain = default;
    static readonly Style Dim = new(0, false, true);
    static readonly Style BoldRed = new(31, true, false);

    [Fact]
    public void Write_ReadsTheRenderersStylesIntoSpans()
    {
        var t = new Transcript();
        t.Write("a \e[2mdim\e[0m \e[1;31mred\e[0m\nnext");

        Assert.Equal([new Span("a ", Plain), new Span("dim", Dim), new Span(" ", Plain), new Span("red", BoldRed)], t.Lines()[0]);
        Assert.Equal([new Span("next", Plain)], t.Lines()[1]);
    }

    [Fact]
    public void Write_Reads256And24BitColors()
    {
        var t = new Transcript();
        t.Write("\e[1;38;5;208morange\e[0m \e[38;2;1;2;3;4mtrue\e[0m");

        Assert.Equal([new Span("orange", new Style(256 + 208, true, false)), new Span(" ", Plain), new Span("true", new Style(Style.Rgb + 0x010203, false, false, Underline: true))], t.Lines()[0]);
        Assert.Equal("\e[1;38;5;208morange\e[0m \e[4;38;2;1;2;3mtrue\e[0m", Transcript.Ansi(t.Lines()[0]));
    }

    [Fact]
    public void Write_ReadsBackgrounds_AndAnErasedLineFillsEveryRowItWrapsTo()
    {
        var t = new Transcript();
        t.Write("\e[41ma\e[49mb \e[1;48;5;236mcode here\e[K\e[0m");

        var shaded = new Style(0, true, false, Background: 256 + 236);
        var line = t.Lines()[0];
        Assert.Equal([new Span("a", new Style(0, false, false, Background: 31)), new Span("b ", Plain), new Span("code here", shaded), new Span("", shaded)], line);
        Assert.All(Transcript.Wrap(line, 7), row => Assert.Equal(new Span("", shaded), row[^1]));
        Assert.EndsWith("\e[1;48;5;236mcode here\e[0m\e[1;48;5;236m\e[K\e[0m", Transcript.Ansi(line));
    }

    [Fact]
    public void Clear_StartsOver_AndTellsTheViewToRebuild()
    {
        var t = new Transcript();
        t.Write("one\n\e[31mtwo");
        var removals = t.Removals;

        t.Clear();
        t.Write("after");

        Assert.Equal([new Span("after", Plain)], Assert.Single(t.Lines()));
        Assert.Equal(removals + 1, t.Removals);
    }

    [Fact]
    public void Write_JoinsStreamedChunksOfTheSameStyle()
    {
        var t = new Transcript();
        t.Write("Hel");
        t.Write("lo");

        Assert.Equal([new Span("Hello", Plain)], Assert.Single(t.Lines()));
    }

    [Fact]
    public void WriteLineAbove_GoesBeforeTheLineStillStreaming()
    {
        var t = new Transcript();
        t.Write("done\nLooking at");
        t.WriteLineAbove("› also this");
        t.Write(" the code\n");

        Assert.Equal(["done", "› also this", "Looking at the code", ""], t.Lines().Select(Text));
    }

    [Fact]
    public void WriteLineAbove_WithNothingStreaming_IsAPlainLine()
    {
        var t = new Transcript();
        t.Write("done\n");
        t.WriteLineAbove("› next");

        Assert.Equal(["done", "› next", ""], t.Lines().Select(Text));
    }

    [Fact]
    public void Section_RemovesOnlyItsOwnLines()
    {
        var t = new Transcript();
        t.Write("? Edit a.cs\n");
        var hide = t.Section(() => t.Write("+new\n-old\n"));
        t.Write("Allow? yes\n");
        var removals = t.Removals;

        hide();

        Assert.Equal(["? Edit a.cs", "Allow? yes", ""], t.Lines().Select(Text));
        Assert.Equal(removals + 1, t.Removals);
    }

    [Fact]
    public void Wrap_BreaksAtSpacesAndKeepsStyles()
    {
        var rows = Transcript.Wrap([new Span("one two ", Plain), new Span("three", Dim)], 9);

        Assert.Equal(["one two ", "three"], rows.Select(Text));
        Assert.Equal(Dim, rows[1][0].Style);
    }

    [Fact]
    public void Wrap_FillsTheRowWithAWordThatFitsNowhere()
    {
        var rows = Transcript.Wrap([new Span("cwd /a/very/long/path", Plain)], 10);

        Assert.Equal(["cwd /a/ver", "y/long/pat", "h"], rows.Select(Text));
    }

    [Fact]
    public void Wrap_AnEmptyLineIsOneRow() => Assert.Single(Transcript.Wrap([], 10));

    [Fact]
    public void Ansi_WritesTheStylesBack()
    {
        var t = new Transcript();
        t.Write("a \e[1;31mred\e[0m\n");

        Assert.Equal("a \e[1;31mred\e[0m\n", t.Ansi(color: true));
        Assert.Equal("a red\n", t.Ansi(color: false));
    }

    [Fact]
    public void Stream_RedrawsTheOpenPartOfTheMessage()
    {
        var t = new Transcript();
        t.Write("before\n");
        t.Stream("", "| a |");
        t.Stream("", "| a |\n| bb |");
        t.Stream("| a  |\n| bb |\n", "Done");
        t.EndStream();
        t.Write("\nafter\n");

        Assert.Equal(["before", "| a  |", "| bb |", "Done", "after", ""], t.Lines().Select(Text));
    }

    [Fact]
    public void Since_StartsFromTheRedrawnMessage()
    {
        var t = new Transcript();
        t.Write("one\ntwo\n");
        t.Stream("", "x");
        var seen = t.Lines().Count;
        t.Since(seen);
        t.Stream("", "x\ny\nz");

        var (_, from, lines) = t.Since(seen);

        Assert.Equal(2, from);
        Assert.Equal(["x", "y", "z"], lines.Select(Text));
    }

    [Fact]
    public void WriteLineAbove_GoesBeforeWhatsStillOpenInTheMessage()
    {
        var t = new Transcript();
        t.Stream("Para one\n", "Para");
        t.WriteLineAbove("› also this");
        t.Stream("Para two\n", "");

        Assert.Equal(["Para one", "› also this", "Para two", ""], t.Lines().Select(Text));
    }

    [Fact]
    public void Section_WhileStreaming_RemovesOnlyItsOwnLines()
    {
        var t = new Transcript();
        t.Stream("", "Looking");
        var hide = t.Section(() => t.WriteLineAbove("+new"));
        hide();
        t.Stream("Looking at it\n", "");

        Assert.Equal(["Looking at it", ""], t.Lines().Select(Text));
    }

    static string Text(List<Span> spans) => string.Concat(spans.Select(s => s.Text));

    static List<string> Texts(Transcript t) =>
        [.. t.Lines().Select(line => string.Concat(line.Select(s => s.Text)))];

    [Fact]
    public void CarriageReturn_ReplacesTheLineWithWhatFollows()
    {
        var t = new Transcript();
        t.Write("saving memory…");
        t.Write($"\r{new string(' ', 30)}\r");
        t.Write("done\n");

        Assert.Equal(["done", ""], Texts(t));
    }

    [Fact]
    public void CarriageReturnBeforeANewline_KeepsTheLine()
    {
        var t = new Transcript();
        t.Write("first\r\nsecond");

        Assert.Equal(["first", "second"], Texts(t));
    }

    [Fact]
    public void EraseLine_ClearsTheCurrentLine()
    {
        var t = new Transcript();
        t.Write("⠋ thinking…\e[2Kanswer");

        Assert.Equal(["answer"], Texts(t));
    }

    [Fact]
    public void EraseScreen_EmptiesTheTranscript()
    {
        var t = new Transcript();
        t.Write("old\nlines\n");
        var removals = t.Removals;

        t.Write("\e[2J\e[3J\e[1;1Hbanner\n");

        Assert.Equal(["banner", ""], Texts(t));
        Assert.Equal(removals + 1, t.Removals);
    }

    [Fact]
    public void Write_WhileAMessageStreams_EndsItSoTheNextRedrawKeepsTheText()
    {
        var t = new Transcript();
        t.Stream("", "half a sen");

        t.Write("⏸ Shell command requested\n");
        Assert.False(t.Streaming);
        t.Stream("", "next message");

        Assert.Equal(["half a sen", "⏸ Shell command requested", "next message"], Texts(t));
    }
}
