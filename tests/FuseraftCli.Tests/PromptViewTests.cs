using fuseraft.Cli.Tui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace FuseraftCli.Tests;

public sealed class PromptViewTests
{
    [Theory]
    [InlineData(KeyCode.Enter | KeyCode.CtrlMask)]
    [InlineData(KeyCode.J | KeyCode.CtrlMask)]
    [InlineData(KeyCode.Enter | KeyCode.AltMask)]
    [InlineData(KeyCode.Enter | KeyCode.ShiftMask)]
    public void New_line_keys_start_a_line_instead_of_sending(KeyCode key)
    {
        var prompt = new PromptView();
        string? sent = null;
        prompt.Sent += text => sent = text;
        prompt.Insert("first");

        prompt.NewKeyDownEvent(new Key(key));
        prompt.Insert("second");

        Assert.Null(sent);
        Assert.Equal("first\nsecond", prompt.Text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Enter_sends()
    {
        var prompt = new PromptView();
        string? sent = null;
        prompt.Sent += text => sent = text;
        prompt.Insert("hello");

        prompt.NewKeyDownEvent(Key.Enter);

        Assert.Equal("hello", sent);
    }
}
