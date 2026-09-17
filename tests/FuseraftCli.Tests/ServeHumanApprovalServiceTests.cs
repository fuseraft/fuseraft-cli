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
        public Task<bool> ReadApprovalResponseAsync() => Task.FromResult(ResponseToReturn);
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
        Assert.True(await service.PromptRouteApprovalAsync("KEYWORD", "A", "B"));
    }
}
