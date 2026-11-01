using fuseraft.Cli.Serve;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers <see cref="ServeHumanApprovalService"/>'s two distribution models: live progress
/// broadcasts to every registered connection, while mutating-action approval gates route only to
/// the current task's originator — and, when no originator is set (an MCP-dispatched task, or one
/// whose dispatching connection has since disconnected), fall back to the unattended policy.
/// </summary>
public sealed class ServeHumanApprovalServiceTests
{
    private sealed class FakeSink : IServeAttachSink
    {
        public readonly List<object> Emitted = [];
        public bool ResponseToReturn = true;

        public void Emit(object payload) => Emitted.Add(payload);

        public Task<bool> RequestApprovalAsync(object request)
        {
            Emitted.Add(request);
            return Task.FromResult(ResponseToReturn);
        }
    }

    [Fact]
    public async Task NoOrigin_DenyPolicy_DeniesMutatingActions()
    {
        var service = new ServeHumanApprovalService(allowWhenUnattended: false);

        Assert.False(await service.PromptShellCommandAsync("echo hi"));
        Assert.False(await service.PromptToolActionAsync("FileSystem", "write_file", "/tmp/x"));
        Assert.False(await service.PromptFileWriteAsync("write_file", "/tmp/x", "old", "new"));
    }

    [Fact]
    public async Task NoOrigin_AllowPolicy_ApprovesMutatingActions()
    {
        var service = new ServeHumanApprovalService(allowWhenUnattended: true);

        Assert.True(await service.PromptShellCommandAsync("echo hi"));
        Assert.True(await service.PromptToolActionAsync("FileSystem", "write_file", "/tmp/x"));
        Assert.True(await service.PromptFileWriteAsync("write_file", "/tmp/x", "old", "new"));
    }

    [Fact]
    public async Task CurrentTaskOrigin_ReceivesTheApprovalRequest_RegardlessOfUnattendedPolicy()
    {
        // Deny is the unattended default — the origin's own answer must still win.
        var service = new ServeHumanApprovalService(allowWhenUnattended: false);
        var origin = new FakeSink { ResponseToReturn = true };
        service.RegisterConnection(origin);
        service.SetCurrentTaskOrigin(origin);

        Assert.True(await service.PromptShellCommandAsync("echo hi"));
        Assert.Single(origin.Emitted);
    }

    [Fact]
    public async Task OriginDenies_BlocksTheAction()
    {
        var service = new ServeHumanApprovalService(allowWhenUnattended: true);
        var origin = new FakeSink { ResponseToReturn = false };
        service.SetCurrentTaskOrigin(origin);

        Assert.False(await service.PromptShellCommandAsync("rm -rf /"));
    }

    [Fact]
    public async Task BystanderConnection_IsNeverAskedToApprove_OnlyTheOriginIs()
    {
        var service = new ServeHumanApprovalService(allowWhenUnattended: false);
        var origin     = new FakeSink { ResponseToReturn = true };
        var bystander  = new FakeSink { ResponseToReturn = true };
        service.RegisterConnection(origin);
        service.RegisterConnection(bystander);
        service.SetCurrentTaskOrigin(origin);

        await service.PromptShellCommandAsync("echo hi");

        Assert.Single(origin.Emitted);
        Assert.Empty(bystander.Emitted);
    }

    [Fact]
    public async Task NoCurrentOrigin_FallsBackToUnattendedPolicy_EvenWithConnectionsRegistered()
    {
        // e.g. an MCP-dispatched task, or an attach connection that dispatched then disconnected —
        // being attached must not make a bystander an approver for work they didn't ask for.
        var service = new ServeHumanApprovalService(allowWhenUnattended: false);
        var bystander = new FakeSink { ResponseToReturn = true };
        service.RegisterConnection(bystander);

        Assert.False(await service.PromptShellCommandAsync("echo hi"));
        Assert.Empty(bystander.Emitted);
    }

    [Fact]
    public void BroadcastEvent_ReachesEveryRegisteredConnection()
    {
        var service = new ServeHumanApprovalService(allowWhenUnattended: false);
        var a = new FakeSink();
        var b = new FakeSink();
        service.RegisterConnection(a);
        service.RegisterConnection(b);

        service.BroadcastEvent(new { type = "agent_starting", agent = "Assistant" });

        Assert.Single(a.Emitted);
        Assert.Single(b.Emitted);
    }

    [Fact]
    public void BroadcastEvent_DoesNotReachAnUnregisteredConnection()
    {
        var service = new ServeHumanApprovalService(allowWhenUnattended: false);
        var sink = new FakeSink();
        service.RegisterConnection(sink);
        service.UnregisterConnection(sink);

        service.BroadcastEvent(new { type = "agent_starting", agent = "Assistant" });

        Assert.Empty(sink.Emitted);
    }

    [Fact]
    public async Task UnreachablePrompts_ResolveAsNoHumanAvailable()
    {
        var service = new ServeHumanApprovalService(allowWhenUnattended: false);

        Assert.Null(await service.PromptContinueAsync());
        Assert.Null(await service.PromptRedirectAsync("Agent"));
        Assert.Null(await service.PromptValidatorStuckAsync("Agent", "Validator", 3, "error"));
        Assert.Null(await service.PromptBlockerResolutionAsync("Agent", "blocked"));
        Assert.Null(await service.PromptPostSessionAsync());
        Assert.Null(await service.PromptPlanReviewAsync("plan"));
    }

    // PromptRouteApprovalAsync gates RequireHumanApproval route/edge decisions — unlike the
    // prompts above (which have no human-attended surface to route to at all), it must follow
    // the same origin-routed / fail-closed-when-unattended convention as the three mutating-
    // action gates, not silently auto-approve regardless of policy or who's attached.

    [Fact]
    public async Task RouteApproval_NoOrigin_DenyPolicy_Denies()
    {
        var service = new ServeHumanApprovalService(allowWhenUnattended: false);

        Assert.False(await service.PromptRouteApprovalAsync("KEYWORD", "A", "B"));
    }

    [Fact]
    public async Task RouteApproval_NoOrigin_AllowPolicy_Approves()
    {
        var service = new ServeHumanApprovalService(allowWhenUnattended: true);

        Assert.True(await service.PromptRouteApprovalAsync("KEYWORD", "A", "B"));
    }

    [Fact]
    public async Task RouteApproval_CurrentTaskOrigin_ReceivesTheRequest_AndItsAnswerWins()
    {
        var service = new ServeHumanApprovalService(allowWhenUnattended: false);
        var origin = new FakeSink { ResponseToReturn = false };
        service.RegisterConnection(origin);
        service.SetCurrentTaskOrigin(origin);

        Assert.False(await service.PromptRouteApprovalAsync("KEYWORD", "A", "B"));
        Assert.Single(origin.Emitted);
    }

    [Fact]
    public async Task ConcurrentGateCalls_AgainstTheSameOrigin_DoNotCrossAnswers()
    {
        // Regression coverage for GateAsync's serialization: two concurrent gate calls (e.g.
        // from a ScatterGather/MapReduce orchestrator's parallel branches) must never have their
        // answers swapped, since IServeAttachSink's wire protocol has no per-request correlation.
        var service = new ServeHumanApprovalService(allowWhenUnattended: false);
        var origin = new SequencedSink([true, false]);
        service.RegisterConnection(origin);
        service.SetCurrentTaskOrigin(origin);

        var t1 = service.PromptShellCommandAsync("echo one");
        var t2 = service.PromptShellCommandAsync("echo two");
        var results = await Task.WhenAll(t1, t2);

        // Order isn't guaranteed, but every call must get exactly one of the two scripted
        // answers, and both scripted answers must be consumed exactly once each.
        Assert.Equal(new[] { false, true }, results.OrderBy(r => r));
        Assert.Equal(2, origin.Emitted.Count);
        Assert.False(origin.Overlapped);
    }

    [Fact]
    public async Task AStoppingDaemon_DeniesThePendingApproval_EvenUnderTheAllowPolicy()
    {
        using var stopping = new CancellationTokenSource();
        var service = new ServeHumanApprovalService(allowWhenUnattended: true) { Stopping = stopping.Token };
        var origin = new SilentSink();
        service.SetCurrentTaskOrigin(origin);
        var gate = service.PromptShellCommandAsync("make deploy");
        await origin.Asked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        stopping.Cancel();

        Assert.False(await gate.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await service.PromptShellCommandAsync("make deploy again"));
    }

    /// <summary>A sink whose client never answers.</summary>
    private sealed class SilentSink : IServeAttachSink
    {
        public readonly TaskCompletionSource Asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(object payload) { }

        public Task<bool> RequestApprovalAsync(object request)
        {
            Asked.TrySetResult();
            return new TaskCompletionSource<bool>().Task;
        }
    }

    /// <summary>A sink that hands out scripted answers in call order, and notes whether two requests were ever in flight at once.</summary>
    private sealed class SequencedSink(IReadOnlyList<bool> responses) : IServeAttachSink
    {
        private int _next;
        private int _active;
        public readonly List<object> Emitted = [];
        public bool Overlapped;

        public void Emit(object payload) => Emitted.Add(payload);

        public async Task<bool> RequestApprovalAsync(object request)
        {
            if (Interlocked.Increment(ref _active) > 1) Overlapped = true;
            lock (Emitted) Emitted.Add(request);
            await Task.Delay(20);
            var index = Interlocked.Increment(ref _next) - 1;
            Interlocked.Decrement(ref _active);
            return responses[index];
        }
    }
}
