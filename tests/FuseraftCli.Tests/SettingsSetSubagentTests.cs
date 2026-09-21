using fuseraft.Cli.Commands;
using fuseraft.Core;
using fuseraft.Infrastructure.Storage;
using Spectre.Console.Cli;

namespace FuseraftCli.Tests;

/// <summary><c>fuseraft settings set subagent.*</c> — the REPL's subagent model and round caps.</summary>
[Collection("FuseraftHomeEnv")]
public sealed class SettingsSetSubagentTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public SettingsSetSubagentTests()
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

    private static fuseraft.Core.Models.Config.SubagentConfig? Saved() => UserConfigStore.Load().Config?.Subagent;

    [Fact]
    public async Task SetExploreCap_Persists()
    {
        Assert.Equal(0, await SetAsync("subagent.exploreMaxIterations", "35"));

        Assert.Equal(35, Saved()!.ExploreMaxIterations);
        Assert.Null(Saved()!.DelegateMaxIterations);
    }

    [Fact]
    public async Task SetDelegateCap_Persists()
    {
        Assert.Equal(0, await SetAsync("subagent.delegateMaxIterations", "60"));

        Assert.Equal(60, Saved()!.DelegateMaxIterations);
        Assert.Null(Saved()!.ExploreMaxIterations);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("many")]
    public async Task CapOutsideOneToOneHundred_IsRejectedAndNothingIsSaved(string value)
    {
        Assert.Equal(1, await SetAsync("subagent.exploreMaxIterations", value));

        Assert.Null(Saved());
    }

    [Fact]
    public async Task DerivedProperties_AreNotWrittenToTheConfigFile()
    {
        await SetAsync("subagent.exploreMaxIterations", "35");

        Assert.DoesNotContain("IsEmpty", File.ReadAllText(UserConfigStore.ConfigPath));
    }

    [Fact]
    public async Task ClearingTheOnlyField_DropsTheSection()
    {
        await SetAsync("subagent.exploreMaxIterations", "35");

        await SetAsync("subagent.exploreMaxIterations", "");

        Assert.Null(Saved());
    }

    [Fact]
    public async Task SettingModel_KeepsTheCaps()
    {
        await SetAsync("subagent.exploreMaxIterations", "35");
        await SetAsync("subagent.delegateMaxIterations", "60");

        await SetAsync("subagent.model", "gpt-4o-mini");

        var saved = Saved()!;
        Assert.Equal("gpt-4o-mini", saved.Model);
        Assert.Equal(35, saved.ExploreMaxIterations);
        Assert.Equal(60, saved.DelegateMaxIterations);
    }

    [Fact]
    public async Task ClearingModel_KeepsTheCaps()
    {
        await SetAsync("subagent.model", "gpt-4o-mini");
        await SetAsync("subagent.delegateMaxIterations", "60");

        await SetAsync("subagent.model", "");

        var saved = Saved()!;
        Assert.Null(saved.Model);
        Assert.Equal(60, saved.DelegateMaxIterations);
    }
}
