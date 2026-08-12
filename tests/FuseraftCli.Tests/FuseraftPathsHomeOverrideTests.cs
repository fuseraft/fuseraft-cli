using fuseraft.Core;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for the <see cref="FuseraftPaths.HomeOverrideEnvVar"/> (<c>FUSERAFT_HOME</c>) escape
/// hatch that relocates the global <c>~/.fuseraft</c> root — e.g. to a network share for
/// RDS/VDI pools where the OS home directory is not durable across sessions.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class FuseraftPathsHomeOverrideTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _original);

    [Fact]
    public void GlobalRoot_WithoutOverride_DefaultsUnderHomeDirectory()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, null);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".fuseraft"), FuseraftPaths.GlobalRoot);
    }

    [Fact]
    public void GlobalRoot_WithOverride_UsesOverridePathVerbatim()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), "fuseraft-share-test");
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, overridePath);
        Assert.Equal(Path.GetFullPath(overridePath), FuseraftPaths.GlobalRoot);
    }

    [Fact]
    public void GlobalRoot_WithTildeOverride_ExpandsAgainstRealHome()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, "~/fuseraft-share");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "fuseraft-share"), FuseraftPaths.GlobalRoot);
    }

    [Fact]
    public void DerivedGlobalPaths_FollowOverride()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), "fuseraft-share-test");
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, overridePath);
        Assert.Equal(Path.Combine(overridePath, "config"), FuseraftPaths.GlobalConfig);
        Assert.Equal(Path.Combine(overridePath, "sessions"), FuseraftPaths.GlobalSessions);
    }

    [Fact]
    public void ExpandPath_OfFuseraftTemplate_FollowsOverride()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), "fuseraft-share-test");
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, overridePath);
        Assert.Equal(
            Path.Combine(overridePath, "logs", "app.log"),
            FuseraftPaths.ExpandPath("~/.fuseraft/logs/app.log"));
    }

    [Fact]
    public void ExpandPath_OfUnrelatedTilde_StillResolvesToRealHome()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), "fuseraft-share-test");
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, overridePath);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".agents", "skills"), FuseraftPaths.ExpandPath("~/.agents/skills"));
    }
}
