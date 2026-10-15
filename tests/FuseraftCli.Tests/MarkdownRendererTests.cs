using Spectre.Console;
using fuseraft.Cli.Display;

namespace FuseraftCli.Tests;

public sealed class MarkdownRendererTests
{
    private static string[] Lines(string markdown, string? lead)
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(output),
            // Spectre's CI enrichers (GITHUB_ACTIONS=true, among others) turn ANSI back on regardless of Ansi.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Width = 80;
        console.Write(MarkdownRenderer.Render(markdown, lead));
        return [.. output.ToString().ReplaceLineEndings("\n").TrimEnd().Split('\n').Select(l => l.TrimEnd())];
    }

    [Fact]
    public void The_lead_starts_the_first_line_of_text()
    {
        Assert.Equal(["◆ Fixed: add now adds.", "• one"], Lines("Fixed: add now adds.\n\n- one", "◆ "));
    }

    [Fact]
    public void A_reply_that_opens_with_a_code_block_gets_the_lead_on_its_own_line()
    {
        var lines = Lines("```python\nx = 1\n```", "◆ ");

        Assert.Equal("◆", lines[0]);
        Assert.Contains(lines, l => l.Contains("x = 1"));
    }

    [Fact]
    public void Without_a_lead_nothing_is_added()
    {
        Assert.Equal(["plain"], Lines("plain", null));
    }
}
