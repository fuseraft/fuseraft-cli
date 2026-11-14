using fuseraft.Cli.Commands;

namespace FuseraftCli.Tests;

/// <summary>
/// Scaffolded agents get the REPL's reading rule: told "get_file_summary first, then startLine/maxLines; never cold-read
/// a large file", grok-4.7 read files that fit in one call in 8–80 line slices.
/// </summary>
public sealed class InitTemplatesReadingTests
{
    public static TheoryData<string> Templates() =>
        ["solo", "research", "pipeline", "swe", "greenfield", "brownfield", "magentic", "debate", "audit", "data", "devops"];

    private static string AllText(string template)
    {
        var config = InitTemplates.Build(template, "gpt-4o", endpoint: null);
        return string.Join('\n', config.AgentFiles.Select(f => f.Content).Prepend(config.MainConfig));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void NoTemplate_SaysToSliceFiles(string template)
    {
        var text = AllText(template);
        Assert.DoesNotContain("never cold-read", text);
        Assert.DoesNotContain("get_file_summary first", text);
    }

    [Theory]
    [InlineData("solo")]
    [InlineData("swe")]
    [InlineData("brownfield")]
    public void FilesThatFit_AreReadWhole(string template) =>
        Assert.Contains("read those in one call, not in pieces", AllText(template));

    [Fact]
    public void TheDeveloper_ReusesAnUnchangedRead() =>
        Assert.Contains("take patch_file's oldText from that read", AllText("brownfield"));
}
