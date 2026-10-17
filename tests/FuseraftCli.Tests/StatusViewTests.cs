using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class StatusViewTests
{
    [Fact]
    public void Keys_are_left_to_the_panel_while_a_question_is_asked()
    {
        Assert.Equal("", StatusView.Keys(working: true, asking: true));
        Assert.Contains("Ctrl+C cancel", StatusView.Keys(working: true, asking: false));
        Assert.Contains("Enter send", StatusView.Keys(working: false, asking: false));
    }
}
