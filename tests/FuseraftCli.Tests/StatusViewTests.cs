using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class StatusViewTests
{
    [Fact]
    public void No_keys_show_while_a_question_is_asked_or_the_session_ends()
    {
        Assert.Equal("", StatusView.Keys(working: true, asking: true));
        Assert.Equal("", StatusView.Keys(working: true, asking: false, ending: true));
        Assert.Contains("Ctrl+C cancel", StatusView.Keys(working: true, asking: false));
        Assert.Contains("Enter send", StatusView.Keys(working: false, asking: false));
    }
}
