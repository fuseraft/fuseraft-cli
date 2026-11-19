using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class ClipboardTests
{
    [Fact]
    public void Osc52_SendsTheTextAsBase64() => Assert.Equal("\e]52;c;aMOpIQ==\a", Clipboard.Osc52("hé!"));

    [Theory]
    [InlineData("  xai-key\r\n", "xai-key")]
    [InlineData("one\ntwo", "one two")]
    public void SingleLine_FitsClipboardTextToAOneLineField(string text, string expected) =>
        Assert.Equal(expected, Clipboard.SingleLine(text));
}
