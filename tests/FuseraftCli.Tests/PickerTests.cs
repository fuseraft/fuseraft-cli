using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class PickerTests
{
    static readonly string[] Models = ["anthropic.claude-haiku-4-5", "anthropic.claude-opus-5-5", "anthropic.claude-sonnet-5", "xai.grok-4.6"];

    static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0', bool control = false) => new(c, key, false, false, control);

    static Picker.Outcome Type(Picker picker, string text)
    {
        var outcome = Picker.Outcome.Continue;
        foreach (var c in text)
            outcome = picker.Handle(Key(ConsoleKey.A, c));
        return outcome;
    }

    static string Highlighted(Picker picker) => picker.Lines().Single(l => l.Highlighted).Text;

    [Fact]
    public void ArrowsMoveAndWrap_EnterChooses()
    {
        var picker = new Picker(Models);

        picker.Handle(Key(ConsoleKey.UpArrow));
        Assert.Equal("› xai.grok-4.6", Highlighted(picker));
        picker.Handle(Key(ConsoleKey.DownArrow));
        picker.Handle(Key(ConsoleKey.DownArrow));

        Assert.Equal(Picker.Outcome.Chosen, picker.Handle(Key(ConsoleKey.Enter)));
        Assert.Equal("anthropic.claude-opus-5-5", picker.Result);
    }

    [Fact]
    public void StartsOnTheSelectedChoice()
    {
        var picker = new Picker(Models, selected: "anthropic.claude-sonnet-5");

        picker.Handle(Key(ConsoleKey.Enter));

        Assert.Equal("anthropic.claude-sonnet-5", picker.Result);
    }

    [Fact]
    public void TypingFilters_AndKeepsTheHighlightWhenItStillMatches()
    {
        var picker = new Picker(Models, selected: "anthropic.claude-sonnet-5");

        Type(picker, "son");
        Assert.Equal(["anthropic.claude-sonnet-5"], picker.Matches);

        picker.Handle(Key(ConsoleKey.Backspace));
        picker.Handle(Key(ConsoleKey.Backspace));
        picker.Handle(Key(ConsoleKey.Backspace));
        Assert.Equal(Models, picker.Matches);
        Assert.Equal("› anthropic.claude-sonnet-5", Highlighted(picker));
    }

    [Fact]
    public void NoMatch_EnterUsesTheTypedNameOnlyWhenAllowed()
    {
        var strict = new Picker(Models);
        Type(strict, "gemini");
        Assert.Equal(Picker.Outcome.Continue, strict.Handle(Key(ConsoleKey.Enter)));

        var open = new Picker(Models, allowTyped: true);
        Type(open, "meta.llama4");
        Assert.Contains(open.Lines(), l => l.Text.Contains("Enter uses \"meta.llama4\""));
        Assert.Equal(Picker.Outcome.Chosen, open.Handle(Key(ConsoleKey.Enter)));
        Assert.Equal("meta.llama4", open.Result);
    }

    [Fact]
    public void EscapeAndCtrlCCancel()
    {
        Assert.Equal(Picker.Outcome.Cancelled, new Picker(Models).Handle(Key(ConsoleKey.Escape)));
        Assert.Equal(Picker.Outcome.Cancelled, new Picker(Models).Handle(Key(ConsoleKey.C, '\x03', control: true)));
    }

    [Fact]
    public void LongListsScroll()
    {
        var many = Enumerable.Range(1, 30).Select(i => $"model-{i:00}").ToArray();
        var picker = new Picker(many, height: 5);

        Assert.Contains(picker.Lines(), l => l.Text == "  ↓ 25 more");
        picker.Handle(Key(ConsoleKey.End));

        var lines = picker.Lines();
        Assert.Contains(lines, l => l.Text == "  ↑ 25 more");
        Assert.Equal("› model-30", Highlighted(picker));
        Assert.Equal(5, lines.Count(l => l.Text.Contains("model-")));
    }
}
