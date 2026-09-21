using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models.Config;

namespace FuseraftCli.Tests;

/// <summary>
/// <c>/provider setup</c> and the first-run wizard save their result straight over the global config.
/// Building that result from scratch used to erase every section the wizard never asks about.
/// </summary>
public sealed class ReplFactoryWizardResultTests
{
    private static UserConfig Configured() => new()
    {
        ModelId = "old-model", Endpoint = "https://old.example/v1", Provider = "openai", ApiKeyEnvVar = "OLD_KEY",
        RequestTimeoutSeconds = 1800, StreamIdleTimeoutSeconds = 600, MaxRetries = 5,
        Sampling = new SamplingDefaultsConfig { Temperature = 0.3 },
        Repl = new ReplDefaultsConfig { NoBanner = true, AutoCompactThreshold = 0.6, MaxIdenticalToolCalls = 9 },
        McpServers = [new McpServerConfig { Name = "docs" }],
        Telemetry = new TelemetryConfig { OtlpEndpoint = "http://collector:4317" },
        Memory = new MemoryExtractionConfig { Model = "gpt-4o-mini" },
        Subagent = new SubagentConfig { DelegateTimeoutMinutes = 45 },
    };

    [Fact]
    public void ChangesOnlyTheProviderConnectionAndModel()
    {
        var r = ReplFactory.ApplyWizardResult(Configured(), "new-model", "https://new.example/v1", "anthropic");

        Assert.Equal("new-model", r.ModelId);
        Assert.Equal("https://new.example/v1", r.Endpoint);
        Assert.Equal("anthropic", r.Provider);
    }

    [Fact]
    public void KeepsEverythingTheWizardNeverAsked()
    {
        var r = ReplFactory.ApplyWizardResult(Configured(), "new-model", "https://new.example/v1", "anthropic");

        Assert.Equal(1800, r.RequestTimeoutSeconds);
        Assert.Equal(600, r.StreamIdleTimeoutSeconds);
        Assert.Equal(5, r.MaxRetries);
        Assert.Equal(0.3, r.Sampling.Temperature);
        Assert.True(r.Repl.NoBanner);
        Assert.Equal(0.6, r.Repl.AutoCompactThreshold);
        Assert.Equal(9, r.Repl.MaxIdenticalToolCalls);
        Assert.Equal("docs", Assert.Single(r.McpServers).Name);
        Assert.Equal("http://collector:4317", r.Telemetry!.OtlpEndpoint);
        Assert.Equal("gpt-4o-mini", r.Memory!.Model);
        Assert.Equal(45, r.Subagent!.DelegateTimeoutMinutes);
    }

    [Fact]
    public void ForgetsTheOldProvidersKeyEnvVar()
    {
        var r = ReplFactory.ApplyWizardResult(Configured(), "new-model", "https://new.example/v1", "anthropic");

        Assert.Equal(string.Empty, r.ApiKeyEnvVar);
    }

    [Fact]
    public void DoesNotMutateTheConfigItWasGiven_SoACancelledWizardLeavesTheSessionAlone()
    {
        var current = Configured();

        ReplFactory.ApplyWizardResult(current, "new-model", "https://new.example/v1", "anthropic");

        Assert.Equal("old-model", current.ModelId);
        Assert.Equal("OLD_KEY", current.ApiKeyEnvVar);
    }

    [Fact]
    public void FirstRun_WithNoExistingConfig_StartsFromDefaults()
    {
        var r = ReplFactory.ApplyWizardResult(null, "m", "http://localhost:11434", "ollama");

        Assert.Equal("m", r.ModelId);
        Assert.Null(r.RequestTimeoutSeconds);
        Assert.Empty(r.McpServers);
    }
}
