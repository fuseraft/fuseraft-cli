using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

public sealed class IncludedRootsStateTests
{
    // Regression guard: orchestration's PluginRegistry.Configure wires a real HITL approveAction
    // into FileSystemPlugin/GitPlugin without ever passing includedRoots, so those instances
    // fall back to IncludedRootsState.Empty. If Empty were an ordinary mutable instance, an
    // approved sandbox-escape grant there would mutate one process-lifetime static singleton —
    // leaking into every other plugin/agent/run that also falls back to it. Empty must never
    // actually grow, no matter how it's called.
    [Fact]
    public void Empty_TryAdd_IsAlwaysANoOp()
    {
        var added = IncludedRootsState.Empty.TryAdd("/some/root");

        Assert.False(added);
        Assert.Empty(IncludedRootsState.Empty.Snapshot());
    }

    [Fact]
    public async Task Empty_DenyOrEscalateAsync_ApprovedGrant_DoesNotPersistAcrossCalls()
    {
        // The current call still succeeds (approval was granted — the denial is swallowed)...
        var outcome = await IncludedRootsState.Empty.DenyOrEscalateAsync(
            PluginResult.Denied("outside sandbox"), "/outside/file.txt", "read_file", (_, _) => Task.FromResult(true));
        Assert.Null(outcome);

        // ...but nothing was actually remembered: Empty is still empty, and a real instance that
        // only sees Empty's (non-)state would still deny the same path on a later call.
        Assert.Empty(IncludedRootsState.Empty.Snapshot());
    }

    [Fact]
    public async Task DenyOrEscalateAsync_NoDenial_ReturnsNullWithoutPrompting()
    {
        var promptCount = 0;
        var outcome = await new IncludedRootsState().DenyOrEscalateAsync(
            null, "/anywhere.txt", "read_file", (_, _) => { promptCount++; return Task.FromResult(true); });

        Assert.Null(outcome);
        Assert.Equal(0, promptCount); // no denial in the first place — never even asks
    }

    [Fact]
    public async Task DenyOrEscalateAsync_DeclinedGrant_ReturnsDistinctBlockedByUserMessage()
    {
        var outcome = await new IncludedRootsState().DenyOrEscalateAsync(
            PluginResult.Denied("outside sandbox"), "/outside/file.txt", "read_file", (_, _) => Task.FromResult(false));

        Assert.NotNull(outcome);
        Assert.Contains("blocked by user", outcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DenyOrEscalateAsync_NoApprovalMechanism_ReturnsOriginalDenial()
    {
        var original = PluginResult.Denied("outside sandbox");
        var outcome = await new IncludedRootsState().DenyOrEscalateAsync(
            original, "/outside/file.txt", "read_file", approveEscape: null);

        Assert.Equal(original, outcome);
    }

    [Fact]
    public void RealInstance_TryAdd_DoesPersist()
    {
        var state = new IncludedRootsState();
        var added = state.TryAdd("/some/root");

        Assert.True(added);
        Assert.Contains("/some/root", state.Snapshot());
    }

    [Fact]
    public void Empty_IsDistinctFromANewlyConstructedInstance()
    {
        // Guards against a future refactor accidentally making `new IncludedRootsState()`
        // itself immutable, or Empty mutable by default.
        var real = new IncludedRootsState();
        Assert.True(real.TryAdd("/x"));
        Assert.False(IncludedRootsState.Empty.TryAdd("/y"));
    }
}
