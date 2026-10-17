using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models;

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

    [Fact]
    public void BareResume_PicksTheNewestSessionFromThisDirectory()
    {
        var here  = Directory.CreateDirectory(Path.Combine(_root, "kiwi")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var t0    = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        ReplSessionSnapshot[] sessions =
        [
            Snapshot("older", here + Path.DirectorySeparatorChar, t0),
            Snapshot("newer", here, t0.AddHours(1)),
            Snapshot("other", other, t0.AddHours(2)),
        ];

        Assert.Equal("newer", ReplCommand.LatestSessionIn(sessions, here)?.SessionId);
    }

    [Fact]
    public void BareResume_NoSessionFromThisDirectory_FindsNothing()
    {
        var other = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;

        Assert.Null(ReplCommand.LatestSessionIn([Snapshot("other", other, DateTime.UtcNow)], _root));
    }

    private static ReplSessionSnapshot Snapshot(string id, string cwd, DateTime updated) =>
        new() { SessionId = id, ModelId = "m", Cwd = cwd, LastUpdatedAt = updated };
}
