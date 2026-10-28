using System.IO.Compression;
using fuseraft.Cli.Commands;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="UpdateCommand"/>'s Windows updater recovery: install.ps1 used to install only fuseraft.exe, so
/// <c>fuseraft update</c> found no fuseraft-update.exe next to it; the release zip has one to restore it from.
/// </summary>
public sealed class UpdateCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft-update-test-{Guid.NewGuid():N}");

    public UpdateCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
        return ms.ToArray();
    }

    [Fact]
    public void ExtractFromZip_FindsEachFileByName_InAnyFolder()
    {
        var zip = Zip(("fuseraft.exe", "cli"), ("bin/fuseraft-update.exe", "updater"));

        Assert.Equal("cli"u8.ToArray(), UpdateCommand.ExtractFromZip(zip, "fuseraft.exe"));
        Assert.Equal("updater"u8.ToArray(), UpdateCommand.ExtractFromZip(zip, UpdateCommand.UpdaterFileName));
        Assert.Null(UpdateCommand.ExtractFromZip(zip, "missing.exe"));
    }

    [Fact]
    public void EnsureUpdater_RestoresAMissingUpdaterFromTheRelease()
    {
        var path = UpdateCommand.EnsureUpdater(_dir, "updater"u8.ToArray());

        Assert.Equal(Path.Combine(_dir, UpdateCommand.UpdaterFileName), path);
        Assert.Equal("updater", File.ReadAllText(path!));
        Assert.False(File.Exists(path + ".new"));
    }

    [Fact]
    public void EnsureUpdater_LeavesAnExistingUpdaterAlone()
    {
        var existing = Path.Combine(_dir, UpdateCommand.UpdaterFileName);
        File.WriteAllText(existing, "installed");

        Assert.Equal(existing, UpdateCommand.EnsureUpdater(_dir, "from release"u8.ToArray()));
        Assert.Equal("installed", File.ReadAllText(existing));
    }

    [Fact]
    public void EnsureUpdater_IsNullWhenMissingAndTheReleaseHasNone() =>
        Assert.Null(UpdateCommand.EnsureUpdater(_dir, null));
}
