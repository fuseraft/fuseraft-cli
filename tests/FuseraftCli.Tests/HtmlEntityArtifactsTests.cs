using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// HTML entities in tool arguments: undo the encoding artifact some models add, without
/// corrupting text where the entity is real. Boomi names a map
/// "ContractManagementSystem Contracts  &amp;gt;  Projects DataManagementSystem.xml" on disk, and
/// its one-line XML stores escaped Groovy such as "a &amp;lt; b".
/// </summary>
public sealed class HtmlEntityArtifactsTests : IDisposable
{
    private const string BoomiName = "Contracts  &gt;  Projects.xml";

    private readonly string _root;

    public HtmlEntityArtifactsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuseraft_entity_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("make &amp;&amp; make test",                 "make && make test")]
    [InlineData("cat a.log | grep x &gt; out.txt",           "cat a.log | grep x > out.txt")]
    [InlineData("echo &quot;hi&quot;",                       "echo \"hi\"")]
    [InlineData("python push.py \"Contracts  &gt;  P.xml\"", "python push.py \"Contracts  &gt;  P.xml\"")]
    [InlineData("ls 'a &amp; b' &amp;&amp; pwd",             "ls 'a &amp; b' && pwd")]
    [InlineData("echo \"say \\\"&amp;\\\"\" &amp;&amp; x",  "echo \"say \\\"&amp;\\\"\" && x")]
    [InlineData("echo it's &amp;&amp; dir",                  "echo it's && dir")]
    [InlineData("plain command",                             "plain command")]
    public void DecodeOutsideQuotes_DecodesShellSyntaxButNotQuotedArguments(string input, string expected) =>
        Assert.Equal(expected, HtmlEntityArtifacts.DecodeOutsideQuotes(input));

    [Fact]
    public async Task ShellRun_QuotedFilenameWithLiteralEntity_ReachesTheRealFile()
    {
        if (OperatingSystem.IsWindows()) return; // uses POSIX cat
        File.WriteAllText(Path.Combine(_root, BoomiName), "map body");
        using var shell = new ShellPlugin(_root);

        var output = await shell.RunAsync($"cat \"{BoomiName}\" &amp;&amp; echo done", _root);

        Assert.Contains("map body", output);
        Assert.Contains("done", output);
    }

    [Fact]
    public void SearchContent_EscapedXmlQuery_MatchesEscapedTextInFile()
    {
        File.WriteAllText(Path.Combine(_root, "map.xml"), "<script>if (a &lt; b) { return x }</script>");

        var result = new SearchPlugin().SearchContent("a &lt; b", _root);

        Assert.Contains("[RESULTS] 1 match(es)", result);
    }

    [Fact]
    public void SearchContent_EntityEncodedQuery_StillMatchesDecodedText()
    {
        File.WriteAllText(Path.Combine(_root, "app.csproj"), "<TargetFramework>net10.0</TargetFramework>");

        var result = new SearchPlugin().SearchContent("&lt;TargetFramework", _root);

        Assert.Contains("[RESULTS] 1 match(es)", result);
    }

    [Fact]
    public async Task GrepFile_MatchesBothForms()
    {
        var file = Path.Combine(_root, "map.xml");
        File.WriteAllText(file, "line one &lt; escaped\nline two < raw\n");
        var plugin = new FileSystemPlugin(sandboxRoot: _root);
        var ops    = new FileSystemManagementOps(plugin, sandboxRoot: _root);

        var result = await ops.GrepFileAsync(file, "&lt;", contextLines: 0);

        Assert.Contains("[2 match(s)", result);
    }
}
