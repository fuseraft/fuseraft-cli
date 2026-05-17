using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Infrastructure;

namespace FuseraftCli.Tests;

// ---------------------------------------------------------------------------
// MemoryManager tests
// ---------------------------------------------------------------------------

public sealed class MemoryManagerTests
{
    // PreTurnAsync — provider returns a block

    [Fact]
    public async Task PreTurnAsync_ReturnsBlock_FromProvider()
    {
        var provider = new StubProvider("recalled facts");
        using var sut = new MemoryManager([provider]);

        var result = await sut.PreTurnAsync("Agent1");

        Assert.Equal("recalled facts", result);
        Assert.Equal(1, provider.LoadCallCount);
    }

    // PreTurnAsync — provider returns null

    [Fact]
    public async Task PreTurnAsync_ReturnsNull_WhenProviderReturnsNull()
    {
        var provider = new StubProvider(null);
        using var sut = new MemoryManager([provider]);

        var result = await sut.PreTurnAsync("Agent1");

        Assert.Null(result);
    }

    // PreTurnAsync — provider throws (should swallow, return null)

    [Fact]
    public async Task PreTurnAsync_SwallowsException_AndReturnsNull()
    {
        var provider = new ThrowingProvider();
        using var sut = new MemoryManager([provider]);

        var result = await sut.PreTurnAsync("Agent1");

        Assert.Null(result);
    }

    // PostTurnAsync — delegates to all providers

    [Fact]
    public async Task PostTurnAsync_CallsAllProviders()
    {
        var p1 = new StubProvider("block1");
        var p2 = new StubProvider("block2");
        using var sut = new MemoryManager([p1, p2]);

        await sut.PostTurnAsync("Agent1", []);

        Assert.Equal(1, p1.SaveCallCount);
        Assert.Equal(1, p2.SaveCallCount);
    }

    // PostTurnAsync — save exception does not propagate

    [Fact]
    public async Task PostTurnAsync_SwallowsSaveException()
    {
        var provider = new ThrowingProvider();
        using var sut = new MemoryManager([provider]);

        // Must not throw
        await sut.PostTurnAsync("Agent1", []);
    }

    // AugmentInstructionsAsync — prepends block when instructions exist

    [Fact]
    public async Task AugmentInstructionsAsync_PrependsBlock_ToExistingInstructions()
    {
        var provider = new StubProvider("MEMORY — facts");
        using var sut = new MemoryManager([provider]);

        var result = await sut.AugmentInstructionsAsync("Agent1", "You are a helpful agent.");

        Assert.Contains("MEMORY — facts", result);
        Assert.Contains("You are a helpful agent.", result);
    }

    // AugmentInstructionsAsync — returns block only when instructions are null

    [Fact]
    public async Task AugmentInstructionsAsync_ReturnsBlockOnly_WhenInstructionsNull()
    {
        var provider = new StubProvider("MEMORY — facts");
        using var sut = new MemoryManager([provider]);

        var result = await sut.AugmentInstructionsAsync("Agent1", null);

        Assert.Equal("MEMORY — facts", result);
    }

    // AugmentInstructionsAsync — returns original instructions when no memory

    [Fact]
    public async Task AugmentInstructionsAsync_ReturnsOriginal_WhenNoMemory()
    {
        var provider = new StubProvider(null);
        using var sut = new MemoryManager([provider]);

        var result = await sut.AugmentInstructionsAsync("Agent1", "instructions");

        Assert.Equal("instructions", result);
    }

    // Multiple providers — blocks are concatenated

    [Fact]
    public async Task PreTurnAsync_ConcatenatesBlocks_FromMultipleProviders()
    {
        var p1 = new StubProvider("block-one");
        var p2 = new StubProvider("block-two");
        using var sut = new MemoryManager([p1, p2]);

        var result = await sut.PreTurnAsync("Agent1");

        Assert.Contains("block-one", result);
        Assert.Contains("block-two", result);
    }

    // CancellationToken propagates through PreTurnAsync

    [Fact]
    public async Task PreTurnAsync_PropagatesCancellation()
    {
        var provider = new CancellingProvider();
        using var sut = new MemoryManager([provider]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.PreTurnAsync("Agent1", cts.Token));
    }

    // FromConfig — returns null for unknown provider

    [Fact]
    public void FromConfig_ReturnsNull_ForUnknownProvider()
    {
        var cfg = new fuseraft.Core.Models.MemoryConfig { Provider = "nonexistent" };
        var result = MemoryManager.FromConfig(cfg);
        Assert.Null(result);
    }

    // FromConfig — returns null for null config

    [Fact]
    public void FromConfig_ReturnsNull_ForNullConfig()
    {
        var result = MemoryManager.FromConfig(null);
        Assert.Null(result);
    }

    // FromConfig — returns null for webhook without Webhook config

    [Fact]
    public void FromConfig_ReturnsNull_ForWebhookWithoutWebhookConfig()
    {
        var cfg = new fuseraft.Core.Models.MemoryConfig { Provider = "webhook" };
        var result = MemoryManager.FromConfig(cfg);
        Assert.Null(result);
    }

    // ---------------------------------------------------------------------------
    // Stubs
    // ---------------------------------------------------------------------------

    private sealed class StubProvider(string? block) : IMemoryProvider
    {
        public int LoadCallCount { get; private set; }
        public int SaveCallCount { get; private set; }

        public Task<string?> LoadAsync(string agentName, CancellationToken ct = default)
        {
            LoadCallCount++;
            return Task.FromResult(block);
        }

        public Task SaveAsync(string agentName, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        {
            SaveCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingProvider : IMemoryProvider
    {
        public Task<string?> LoadAsync(string agentName, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated load failure");

        public Task SaveAsync(string agentName, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated save failure");
    }

    private sealed class CancellingProvider : IMemoryProvider
    {
        public Task<string?> LoadAsync(string agentName, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }

        public Task SaveAsync(string agentName, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
