using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class ClipboardTests
{
    [Fact]
    public void Osc52_SendsTheTextAsBase64() => Assert.Equal("\e]52;c;aMOpIQ==\a", Clipboard.Osc52("hé!"));
}
