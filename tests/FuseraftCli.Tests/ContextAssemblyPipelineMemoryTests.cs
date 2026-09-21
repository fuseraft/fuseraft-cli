using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Agents;
using fuseraft.Infrastructure.Memory;
using fuseraft.Orchestration.Context;

namespace FuseraftCli.Tests;

/// <summary>
/// Drives the real pipeline → <see cref="MemoryManager"/> → <c>LocalMemoryProvider</c> → <see cref="MemoryStore"/>
/// chain, because relevance ranking was once implemented and then silently unwired by a refactor.
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ContextAssemblyPipelineMemoryTests : IDisposable
{
    private readonly string  _home = Path.Combine(Path.GetTempPath(), $"fuseraft_pipemem_{Guid.NewGuid():N}");
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);

    public ContextAssemblyPipelineMemoryTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        Directory.Delete(_home, recursive: true);
    }

    private static async Task SeedOverBudgetStoreAsync(string agent)
    {
        var store = MemoryStore.ForAgent(agent);
        for (int i = 0; i < 30; i++)
            await store.SaveAsync(new MemoryEntry
            {
                Name = $"entry_{i:D2}", Description = $"Desc {i}", Type = "project", Body = new string('x', 500),
            });
        await store.SaveAsync(new MemoryEntry
        {
            Name = "zz_AuthMiddleware_refresh", Description = "AuthMiddleware refresh quirks",
            Type = "reference", Body = "QUIRK_MARKER",
        });
    }

    private static AgentExecutionRequest Request(string agent, string task) => new()
    {
        AgentName     = agent,
        Task          = task,
        SharedHistory = [],
        AgentConfig   = new AgentConfig { Name = agent },
    };

    [Fact]
    public async Task AssembleAsync_TaskTermsRankMemory_SoRelevantEntrySurvivesTheBudget()
    {
        await SeedOverBudgetStoreAsync("Fixer");
        using var manager = MemoryManager.FromConfig(new MemoryConfig { Provider = "local" })!;
        var pipeline = new ContextAssemblyPipeline(memoryManager: manager);

        var assembled = await pipeline.AssembleAsync(
            Request("Fixer", "Fix the AuthMiddleware refresh bug"));

        Assert.Contains("QUIRK_MARKER", assembled.SystemPrompt);
    }

    [Fact]
    public async Task AssembleAsync_UnrelatedTask_DoesNotPromoteTheEntry()
    {
        await SeedOverBudgetStoreAsync("Fixer");
        using var manager = MemoryManager.FromConfig(new MemoryConfig { Provider = "local" })!;
        var pipeline = new ContextAssemblyPipeline(memoryManager: manager);

        var assembled = await pipeline.AssembleAsync(
            Request("Fixer", "Summarize the quarterly billing statements"));

        Assert.DoesNotContain("QUIRK_MARKER", assembled.SystemPrompt);
    }
}
