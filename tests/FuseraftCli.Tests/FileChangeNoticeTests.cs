using fuseraft.Infrastructure.Context;

namespace FuseraftCli.Tests;

/// <summary>What the agent is told about changed files: their names, never their contents.</summary>
public sealed class FileChangeNoticeTests
{
    private static FileChange Changed(string path) => new(path, Deleted: false);

    [Fact]
    public void Outside_NamesTheFile_AndAsksForARead()
    {
        var notice = FileChangeNotice.Build([Changed("/repo/a.cs")], during: null);

        Assert.Contains("changed outside fuseraft", notice);
        Assert.Contains("`/repo/a.cs` changed.", notice);
        Assert.Contains("write_file on them is refused", notice);
    }

    [Fact]
    public void During_NamesTheTool()
    {
        var notice = FileChangeNotice.Build([Changed("/repo/a.cs")], during: "shell_run");

        Assert.Contains("while shell_run ran", notice);
    }

    [Fact]
    public void Deleted_SaysSo_WithoutAskingForARead()
    {
        var notice = FileChangeNotice.Build([new FileChange("/repo/a.cs", Deleted: true)], null);

        Assert.Contains("was deleted", notice);
        Assert.DoesNotContain("Re-read", notice);
    }

    [Fact]
    public void ManyFiles_AreCapped()
    {
        var changes = Enumerable.Range(0, FileChangeNotice.MaxFiles + 2).Select(i => Changed($"/repo/{i}.txt")).ToList();

        Assert.Contains("and 2 more", FileChangeNotice.Build(changes, "shell_run"));
    }
}
