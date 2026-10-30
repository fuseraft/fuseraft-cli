using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class ReplTuiTests
{
    [Fact]
    public void Arrange_stacks_transcript_queue_suggestions_rule_prompt_and_status()
    {
        var frame = ReplTui.Arrange(Dock.Bottom, height: 30, rows: 2, suggested: 3, queued: 2);

        Assert.Equal(new ReplTui.Frame(Transcript: 0, TranscriptHeight: 21, Queue: 21, Suggest: 23, Rule: 26, Prompt: 27, Status: 29), frame);
    }

    [Fact]
    public void Arrange_docked_on_top_mirrors_it()
    {
        var frame = ReplTui.Arrange(Dock.Top, height: 30, rows: 2, suggested: 3, queued: 2);

        Assert.Equal(new ReplTui.Frame(Transcript: 9, TranscriptHeight: 21, Queue: 7, Suggest: 4, Rule: 3, Prompt: 1, Status: 0), frame);
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

    [Fact]
    public void A_paste_too_long_for_the_prompt_is_folded_into_a_mark()
    {
        Assert.Null(ReplTui.PasteMark("one\ntwo\n", 1));
        Assert.Equal("[pasted #2 · 12 lines]", ReplTui.PasteMark(string.Join("\n", Enumerable.Range(1, 12)), 2));
        Assert.Equal("[pasted #3 · 2,500 chars]", ReplTui.PasteMark(new string('x', 2500), 3));
    }

    [Fact]
    public void Count_shortens_token_counts()
    {
        Assert.Equal("950", ReplTui.Count(950));
        Assert.Equal("48.2k", ReplTui.Count(48_200));
        Assert.Equal("1M", ReplTui.Count(999_600));
        Assert.Equal("1.25M", ReplTui.Count(1_250_000));
    }

    [Fact]
    public void Sent_finds_the_lines_that_start_a_message_the_user_sent()
    {
        List<List<Span>> lines = [[new("› ", new Style(36)), new("fix it", default)], [new("◆ Done.", default)], [], [new("› /help", default)]];

        Assert.Equal([0, 3], ReplTui.Sent(lines));
    }

    [Fact]
    public void A_queued_row_puts_a_message_on_one_line_and_counts_what_doesnt_fit()
    {
        List<string> queued = ["/help", "first\nsecond", "c", "d"];

        Assert.Equal("  queued › first ⏎ second", QueueView.Row(queued, 1));
        Assert.Equal("  queued · 2 more", QueueView.Row(queued, 2));
        Assert.Equal("  queued › c", QueueView.Row(queued[..3], 2));
    }
}
