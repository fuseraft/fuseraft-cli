using fuseraft.Core;

namespace FuseraftCli.Tests;

/// <summary>
/// Older fuseraft versions had a plain-text keychain fallback that wrote the API key to
/// <c>~/.fuseraft/.key</c>. fuseraft no longer writes that file, and <see cref="UserConfigStore.Load"/>
/// now scrubs any leftover copy from disk on every call so the plaintext key can't persist across
/// an upgrade — these tests pin that cleanup behavior using an isolated FUSERAFT_HOME.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class UserConfigStoreLegacyKeyFileTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public UserConfigStoreLegacyKeyFileTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    [Fact]
    public void Load_WithLeftoverKeyFile_ReturnsKeyAndDeletesFile()
    {
        Directory.CreateDirectory(FuseraftPaths.GlobalRoot);
        File.WriteAllText(FuseraftPaths.GlobalKeyFile, "sk-legacy-plaintext-key");

        var (_, legacyKey) = UserConfigStore.Load();

        Assert.Equal("sk-legacy-plaintext-key", legacyKey);
        Assert.False(File.Exists(FuseraftPaths.GlobalKeyFile));
    }

    [Fact]
    public void Load_WithoutKeyFile_ReturnsNullLegacyKey()
    {
        var (config, legacyKey) = UserConfigStore.Load();

        Assert.Null(config);
        Assert.Null(legacyKey);
    }
}
