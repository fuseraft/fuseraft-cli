using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class ReplTuiTests
{
    [Fact]
    public void Arrange_stacks_transcript_suggestions_rule_prompt_and_status()
    {
        var frame = ReplTui.Arrange(Dock.Bottom, height: 30, rows: 2, suggested: 3);

        Assert.Equal(new ReplTui.Frame(Transcript: 0, TranscriptHeight: 23, Suggest: 23, Rule: 26, Prompt: 27, Status: 29), frame);
    }

    [Fact]
    public void Arrange_docked_on_top_mirrors_it()
    {
        var frame = ReplTui.Arrange(Dock.Top, height: 30, rows: 2, suggested: 3);

        Assert.Equal(new ReplTui.Frame(Transcript: 7, TranscriptHeight: 23, Suggest: 4, Rule: 3, Prompt: 1, Status: 0), frame);
    }

    [Fact]
    public void Arrange_never_gives_the_transcript_negative_rows()
    {
        Assert.Equal(0, ReplTui.Arrange(Dock.Bottom, height: 3, rows: 10, suggested: 0).TranscriptHeight);
    }

    [Fact]
    public void Matches_finds_lines_ignoring_case()
    {
        List<List<Span>> lines = [[new("Hello", default)], [new("nothing", default)], [new("say ", default), new("HELLO", default)]];

        Assert.Equal([0, 2], ReplTui.Matches(lines, "hello"));
    }

    [Fact]
    public void Rings_only_once_the_user_has_been_away()
    {
        var now = DateTime.UtcNow;

        Assert.False(ReplTui.ShouldRing(true, now.AddSeconds(-2), now));
        Assert.True(ReplTui.ShouldRing(true, now - ReplTui.Away, now));
        Assert.False(ReplTui.ShouldRing(false, now - ReplTui.Away, now));
    }
}
