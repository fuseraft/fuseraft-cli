using fuseraft.Cli.Commands;
using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Storage;
using Spectre.Console.Cli;

namespace FuseraftCli.Tests;

/// <summary><c>fuseraft settings set repl.hitlAutoApproveReadOnly</c> — the persisted form of <c>/hitl auto</c>.</summary>
[Collection("FuseraftHomeEnv")]
public sealed class SettingsSetHitlAutoApproveTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public SettingsSetHitlAutoApproveTests()
    {
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    // Command<T>'s async entry point is only exposed through the interface.
    private static async Task<int> SetAsync(string key, string value) =>
        await ((ICommand<SettingsSetSettings>)new SettingsSetCommand())
            .ExecuteAsync(null!, new SettingsSetSettings { Key = key, Value = value }, CancellationToken.None);

    [Fact]
    public void ReplDefaults_HitlAutoApproveReadOnly_DefaultsToFalse() =>
        Assert.False(new ReplDefaultsConfig().HitlAutoApproveReadOnly);

    [Fact]
    public async Task SetTrue_PersistsTheDefault()
    {
        var exit = await SetAsync("repl.hitlAutoApproveReadOnly", "true");

        Assert.Equal(0, exit);
        Assert.True(UserConfigStore.Load().Config!.Repl.HitlAutoApproveReadOnly);
    }

    [Fact]
    public async Task SetTrueThenFalse_TurnsItBackOff()
    {
        await SetAsync("repl.hitlAutoApproveReadOnly", "true");

        var exit = await SetAsync("repl.hitlAutoApproveReadOnly", "false");

        Assert.Equal(0, exit);
        Assert.False(UserConfigStore.Load().Config!.Repl.HitlAutoApproveReadOnly);
    }

    [Fact]
    public async Task KeyIsCaseInsensitive_LikeTheOtherReplKeys()
    {
        var exit = await SetAsync("REPL.HitlAutoApproveReadOnly", "true");

        Assert.Equal(0, exit);
        Assert.True(UserConfigStore.Load().Config!.Repl.HitlAutoApproveReadOnly);
    }

    [Fact]
    public async Task ANonBooleanValue_IsRejectedAndNothingIsSaved()
    {
        var exit = await SetAsync("repl.hitlAutoApproveReadOnly", "sometimes");

        Assert.NotEqual(0, exit);
        Assert.False(UserConfigStore.Load().Config?.Repl.HitlAutoApproveReadOnly ?? false);
    }

    [Fact]
    public async Task TheSettingRoundTripsThroughTheConfigFile_UnderItsDocumentedJsonName()
    {
        await SetAsync("repl.hitlAutoApproveReadOnly", "true");

        var json = await File.ReadAllTextAsync(Path.Combine(_tempHome, "config"));

        Assert.Contains("\"hitlAutoApproveReadOnly\": true", json.Replace("\r", ""));
    }
}
