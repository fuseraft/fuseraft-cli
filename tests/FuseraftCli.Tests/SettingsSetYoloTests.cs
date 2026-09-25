using fuseraft.Cli.Commands;
using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Storage;
using Spectre.Console.Cli;

namespace FuseraftCli.Tests;

/// <summary><c>fuseraft settings set repl.yolo</c> — the persisted form of <c>--yolo</c>.</summary>
[Collection("FuseraftHomeEnv")]
public sealed class SettingsSetYoloTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public SettingsSetYoloTests()
    {
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    private static async Task<int> SetAsync(string key, string value) =>
        await ((ICommand<SettingsSetSettings>)new SettingsSetCommand())
            .ExecuteAsync(null!, new SettingsSetSettings { Key = key, Value = value }, CancellationToken.None);

    [Fact]
    public void ReplDefaults_Yolo_DefaultsToFalse() =>
        Assert.False(new ReplDefaultsConfig().Yolo);

    [Fact]
    public async Task SetTrueThenFalse_PersistsEach()
    {
        Assert.Equal(0, await SetAsync("repl.yolo", "true"));
        Assert.True(UserConfigStore.Load().Config!.Repl.Yolo);

        Assert.Equal(0, await SetAsync("repl.yolo", "false"));
        Assert.False(UserConfigStore.Load().Config!.Repl.Yolo);
    }

    [Fact]
    public async Task NonBooleanValue_IsRejected_AndNothingIsSaved()
    {
        Assert.NotEqual(0, await SetAsync("repl.yolo", "maybe"));
        Assert.False(UserConfigStore.Load().Config?.Repl.Yolo ?? false);
    }
}
