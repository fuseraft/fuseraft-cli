using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

public sealed class ReplResumeDirectoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fuseraft-resume-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void SessionFromAnotherExistingDirectory_MovesThere()
    {
        var saved = Directory.CreateDirectory(Path.Combine(_root, "kiwi")).FullName;
        var here  = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;

        Assert.Equal(saved, ReplCommand.ResumeDirectory(saved, here));
    }

    [Fact]
    public void SameDirectory_StaysPut_EvenWithATrailingSeparator()
    {
        var here = Directory.CreateDirectory(Path.Combine(_root, "kiwi")).FullName;

        Assert.Null(ReplCommand.ResumeDirectory(here + Path.DirectorySeparatorChar, here));
    }

    [Fact]
    public void SavedDirectoryGone_OrUnrecorded_StaysPut()
    {
        Assert.Null(ReplCommand.ResumeDirectory(Path.Combine(_root, "deleted"), _root));
        Assert.Null(ReplCommand.ResumeDirectory(null, _root));
        Assert.Null(ReplCommand.ResumeDirectory("", _root));
    }
}
