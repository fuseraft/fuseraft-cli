using fuseraft.Infrastructure.Context;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>What the agent is told about changed files: a diff for changes made outside fuseraft, none for its own tool's.</summary>
public sealed class FileChangeNoticeTests
{
    private static FileChange Changed(string path, string before, string after, params string[] secrets) =>
        new(path, Deleted: false, before, after, new SortedSet<string>(secrets));

    [Fact]
    public void Outside_ShowsTheChangedLines()
    {
        var notice = FileChangeNotice.Build([Changed("/repo/a.cs", "a\nb\nc\n", "a\nB\nc\n")], during: null);

        Assert.Contains("changed outside fuseraft", notice);
        Assert.Contains("(+1/−1 lines)", notice);
        Assert.Contains("   - b", notice);
        Assert.Contains("   + B", notice);
        Assert.Contains("write_file on them is refused", notice);
    }

    [Fact]
    public void During_NamesTheTool_WithoutADiff()
    {
        var notice = FileChangeNotice.Build([Changed("/repo/a.cs", "a\n", "b\n")], during: "shell_run");

        Assert.Contains("while shell_run ran", notice);
        Assert.DoesNotContain("   + b", notice);
    }

    [Fact]
    public void Deleted_SaysSo_WithoutAskingForARead()
    {
        var notice = FileChangeNotice.Build([new FileChange("/repo/a.cs", Deleted: true, "a", null, new SortedSet<string>())], null);

        Assert.Contains("was deleted", notice);
        Assert.DoesNotContain("Re-read", notice);
    }

    [Fact]
    public void ASecretInTheDiff_IsMasked_AndNamed()
    {
        const string name = "FUSERAFT_TEST_NOTICE_API_KEY";
        const string value = "notice-test-0123456789abcdef";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            var notice = FileChangeNotice.Build([Changed("/repo/.cfg", "a\n", $"a\nkey={value}\n", name)], during: null);

            Assert.DoesNotContain(value, notice);
            Assert.Contains($"key={EnvSecretMasker.Placeholder}", notice);
            Assert.Contains($"value of {name}", notice);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void ManyFiles_AreCapped()
    {
        var changes = Enumerable.Range(0, FileChangeNotice.MaxFiles + 2).Select(i => Changed($"/repo/{i}.txt", "a", "b")).ToList();

        Assert.Contains("and 2 more", FileChangeNotice.Build(changes, "shell_run"));
    }

    [Fact]
    public void Diff_IsTheBlockBetweenTheCommonHeadAndTail()
    {
        var (removed, added) = FileChangeNotice.Diff("1\n2\n3\n4\n", "1\nX\nY\n4\n")!.Value;

        Assert.Equal(["2", "3"], removed);
        Assert.Equal(["X", "Y"], added);
    }
}
