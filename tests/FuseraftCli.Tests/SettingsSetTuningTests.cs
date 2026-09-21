using fuseraft.Cli.Commands;
using fuseraft.Core;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Storage;
using Spectre.Console.Cli;

namespace FuseraftCli.Tests;

/// <summary>
/// <c>fuseraft settings set</c> for the transport, REPL-threshold, subagent-timeout and
/// memory/subagent connection keys — accepted values persist and round-trip; out-of-range ones are
/// rejected without touching the file; clearing restores the built-in default.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class SettingsSetTuningTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public SettingsSetTuningTests()
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

    private static UserConfig Saved() => UserConfigStore.Load().Config ?? new UserConfig();

    // ── provider transport ───────────────────────────────────────────────────

    [Fact]
    public async Task TransportKeys_PersistInTheProviderSection_AndRoundTrip()
    {
        Assert.Equal(0, await SetAsync("provider.requestTimeoutSeconds", "1800"));
        Assert.Equal(0, await SetAsync("provider.streamIdleTimeoutSeconds", "600"));
        Assert.Equal(0, await SetAsync("provider.maxRetries", "5"));

        var cfg = Saved();
        Assert.Equal(1800, cfg.RequestTimeoutSeconds);
        Assert.Equal(600, cfg.StreamIdleTimeoutSeconds);
        Assert.Equal(5, cfg.MaxRetries);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(UserConfigStore.ConfigPath));
        var provider = doc.RootElement.GetProperty("provider");
        Assert.Equal(1800, provider.GetProperty("requestTimeoutSeconds").GetInt32());
        Assert.Equal(5, provider.GetProperty("maxRetries").GetInt32());
    }

    [Fact]
    public async Task ZeroRetries_IsAValidValueNotTheSameAsUnset()
    {
        await SetAsync("provider.maxRetries", "0");

        Assert.Equal(0, Saved().MaxRetries);
    }

    [Theory]
    [InlineData("provider.requestTimeoutSeconds", "29")]
    [InlineData("provider.requestTimeoutSeconds", "7201")]
    [InlineData("provider.streamIdleTimeoutSeconds", "10")]
    [InlineData("provider.streamIdleTimeoutSeconds", "3601")]
    [InlineData("provider.maxRetries", "-1")]
    [InlineData("provider.maxRetries", "11")]
    [InlineData("provider.maxRetries", "lots")]
    public async Task TransportOutOfRange_IsRejectedAndNothingIsSaved(string key, string value)
    {
        Assert.Equal(1, await SetAsync(key, value));

        Assert.False(File.Exists(UserConfigStore.ConfigPath));
    }

    [Fact]
    public async Task ClearingATransportKey_RestoresTheDefault_AndDropsItFromTheFile()
    {
        await SetAsync("provider.requestTimeoutSeconds", "1800");

        await SetAsync("provider.requestTimeoutSeconds", "");

        Assert.Null(Saved().RequestTimeoutSeconds);
        Assert.DoesNotContain("requestTimeoutSeconds", File.ReadAllText(UserConfigStore.ConfigPath));
    }

    // ── repl thresholds ──────────────────────────────────────────────────────

    [Fact]
    public async Task ReplThresholdKeys_Persist()
    {
        Assert.Equal(0, await SetAsync("repl.autoCompactThreshold", "0.6"));
        Assert.Equal(0, await SetAsync("repl.compactPreserveTailRatio", "0.3"));
        Assert.Equal(0, await SetAsync("repl.maxConsecutiveToolFailures", "8"));
        Assert.Equal(0, await SetAsync("repl.maxIdenticalToolCalls", "9"));
        Assert.Equal(0, await SetAsync("repl.warnIdenticalToolCalls", "6"));
        Assert.Equal(0, await SetAsync("repl.maxStreamRetries", "4"));

        var repl = Saved().Repl;
        Assert.Equal(0.6, repl.AutoCompactThreshold);
        Assert.Equal(0.3, repl.CompactPreserveTailRatio);
        Assert.Equal(8, repl.MaxConsecutiveToolFailures);
        Assert.Equal(9, repl.MaxIdenticalToolCalls);
        Assert.Equal(6, repl.WarnIdenticalToolCalls);
        Assert.Equal(4, repl.MaxStreamRetries);
    }

    [Theory]
    [InlineData("repl.autoCompactThreshold", "0.49")]
    [InlineData("repl.autoCompactThreshold", "0.96")]
    [InlineData("repl.autoCompactThreshold", "high")]
    [InlineData("repl.compactPreserveTailRatio", "0.04")]
    [InlineData("repl.compactPreserveTailRatio", "0.51")]
    [InlineData("repl.maxConsecutiveToolFailures", "1")]
    [InlineData("repl.maxConsecutiveToolFailures", "51")]
    [InlineData("repl.maxIdenticalToolCalls", "2")]
    [InlineData("repl.maxIdenticalToolCalls", "51")]
    [InlineData("repl.warnIdenticalToolCalls", "1")]
    [InlineData("repl.warnIdenticalToolCalls", "50")]
    [InlineData("repl.maxStreamRetries", "-1")]
    [InlineData("repl.maxStreamRetries", "6")]
    public async Task ReplThresholdOutOfRange_IsRejectedAndNothingIsSaved(string key, string value)
    {
        Assert.Equal(1, await SetAsync(key, value));

        Assert.False(File.Exists(UserConfigStore.ConfigPath));
    }

    [Fact]
    public async Task ClearingAReplThreshold_LeavesTheOtherSettingsAlone()
    {
        await SetAsync("repl.noBanner", "true");
        await SetAsync("repl.autoCompactThreshold", "0.6");

        await SetAsync("repl.autoCompactThreshold", "");

        var repl = Saved().Repl;
        Assert.Null(repl.AutoCompactThreshold);
        Assert.True(repl.NoBanner);
    }

    [Fact]
    public async Task UnsetReplThresholds_AreNotWrittenAsNulls()
    {
        await SetAsync("repl.noBanner", "true");

        var json = File.ReadAllText(UserConfigStore.ConfigPath);

        Assert.DoesNotContain("autoCompactThreshold", json);
        Assert.DoesNotContain("maxIdenticalToolCalls", json);
    }

    // ── subagent timeouts ────────────────────────────────────────────────────

    [Fact]
    public async Task SubagentTimeouts_Persist_AndDoNotDisturbTheCaps()
    {
        await SetAsync("subagent.exploreMaxIterations", "30");

        Assert.Equal(0, await SetAsync("subagent.exploreTimeoutMinutes", "12"));
        Assert.Equal(0, await SetAsync("subagent.locateTimeoutMinutes", "3"));
        Assert.Equal(0, await SetAsync("subagent.delegateTimeoutMinutes", "45"));

        var sub = Saved().Subagent!;
        Assert.Equal(12, sub.ExploreTimeoutMinutes);
        Assert.Equal(3, sub.LocateTimeoutMinutes);
        Assert.Equal(45, sub.DelegateTimeoutMinutes);
        Assert.Equal(30, sub.ExploreMaxIterations);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("241")]
    [InlineData("soon")]
    public async Task SubagentTimeoutOutOfRange_IsRejected(string value)
    {
        Assert.Equal(1, await SetAsync("subagent.delegateTimeoutMinutes", value));

        Assert.Null(Saved().Subagent);
    }

    [Fact]
    public async Task ClearingTheOnlySubagentTimeout_DropsTheSection()
    {
        await SetAsync("subagent.locateTimeoutMinutes", "3");

        await SetAsync("subagent.locateTimeoutMinutes", "");

        Assert.Null(Saved().Subagent);
    }

    // ── memory / subagent connection overrides ───────────────────────────────

    [Fact]
    public async Task SubagentConnection_Persists_NextToTheModel()
    {
        await SetAsync("subagent.model", "gpt-4o-mini");
        await SetAsync("subagent.endpoint", "https://litellm.example/v1");
        await SetAsync("subagent.provider", "openai");
        await SetAsync("subagent.apiKeyEnvVar", "LITELLM_KEY");

        var sub = Saved().Subagent!;
        Assert.Equal("gpt-4o-mini", sub.Model);
        Assert.Equal("https://litellm.example/v1", sub.Endpoint);
        Assert.Equal("openai", sub.Provider);
        Assert.Equal("LITELLM_KEY", sub.ApiKeyEnvVar);
    }

    [Fact]
    public async Task MemoryConnection_Persists_AndSettingTheModelKeepsIt()
    {
        await SetAsync("memory.endpoint", "https://litellm.example/v1");
        await SetAsync("memory.apiKeyEnvVar", "LITELLM_KEY");

        await SetAsync("memory.model", "gpt-4o-mini");

        var mem = Saved().Memory!;
        Assert.Equal("gpt-4o-mini", mem.Model);
        Assert.Equal("https://litellm.example/v1", mem.Endpoint);
        Assert.Equal("LITELLM_KEY", mem.ApiKeyEnvVar);
    }

    [Fact]
    public async Task ClearingTheMemoryModel_KeepsItsConnection_AndClearingEverythingDropsTheSection()
    {
        await SetAsync("memory.model", "gpt-4o-mini");
        await SetAsync("memory.endpoint", "https://litellm.example/v1");

        await SetAsync("memory.model", "");
        Assert.Equal("https://litellm.example/v1", Saved().Memory!.Endpoint);

        await SetAsync("memory.endpoint", "");
        Assert.Null(Saved().Memory);
    }

    [Fact]
    public async Task OverrideSections_DoNotWriteUnsetFieldsOrDerivedProperties()
    {
        await SetAsync("subagent.endpoint", "https://litellm.example/v1");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(UserConfigStore.ConfigPath));
        var written = doc.RootElement.GetProperty("subagent").EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(["Endpoint"], written);
    }
}
