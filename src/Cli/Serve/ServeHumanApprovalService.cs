using fuseraft.Core.Events;
using fuseraft.Core.Interfaces;

namespace fuseraft.Cli.Serve;

/// <summary>
/// Receiving end of messages emitted by <see cref="ServeHumanApprovalService"/> to a human
/// attached over the socket (<c>fuseraft attach</c>). Implemented by the per-connection handler
/// in <see cref="ServeSocketListener"/>, which wraps the socket's <c>NetworkStream</c> in the
/// same JSONL protocol <c>ReplJsonBridge</c>/<c>ReplStdinPump</c> use for the VS Code webview.
/// One generic <see cref="Emit"/> method is used for both approval requests (targeted at one
/// connection — see <see cref="ServeHumanApprovalService.SetCurrentTaskOrigin"/>) and live
/// progress events (broadcast to every connection) — the wire shape is identical either way,
/// just a JSON line with a <c>type</c> discriminator; only the routing differs.
/// </summary>
public interface IServeAttachSink
{
    void Emit(object payload);
    Task<bool> ReadApprovalResponseAsync();
}

/// <summary>
/// <see cref="IHumanApprovalService"/> for <c>fuseraft serve</c>. Two independent distribution
/// models share the same set of attached connections:
///
/// <list type="bullet">
/// <item><description>
/// <b>Live progress</b> (<see cref="BroadcastEvent"/>) goes to every attached connection —
/// anyone watching can see what the daemon is currently doing, regardless of who dispatched it.
/// </description></item>
/// <item><description>
/// <b>Approval requests</b> (the three mutating-action gates) go only to the <em>originator</em>
/// of the task currently running (<see cref="SetCurrentTaskOrigin"/>, set by <c>ServeHost</c>
/// right before invoking the orchestrator for that task) — never to a bystander who merely
/// happens to be attached. A task with no attach-connection origin (dispatched over MCP, or
/// whose dispatching connection has since disconnected) falls through to the unattended policy.
/// </description></item>
/// </list>
///
/// <para>
/// The unattended policy itself deliberately diverges from
/// <see cref="NonInteractiveHumanApprovalService"/>'s CI-oriented auto-approve: that service
/// exists for eval/CI runs a human explicitly launched and is choosing not to babysit, whereas an
/// MCP-dispatched task is one where a human may never even know it ran. Fail-closed here mirrors
/// the project's existing default-deny precedents (`.env` access denied by default, `--yolo`
/// required to open the REPL sandbox) rather than the CI precedent. Set
/// <paramref name="allowWhenUnattended"/> to opt back into auto-approve for trusted unattended
/// pipelines (<c>fuseraft serve --unattended-policy allow</c>).
/// </para>
///
/// <para>
/// All other <see cref="IHumanApprovalService"/> members follow the same "no human available on
/// this surface" convention <see cref="JsonBridgeHumanApprovalService"/> and
/// <see cref="NonInteractiveHumanApprovalService"/> already use — they resolve as
/// continue/approve-as-is rather than blocking on a prompt nothing could ever answer.
/// </para>
/// </summary>
public sealed class ServeHumanApprovalService(bool allowWhenUnattended) : IHumanApprovalService
{
    private readonly Lock _lock = new();
    private readonly List<IServeAttachSink> _connections = [];
    private IServeAttachSink? _currentTaskOrigin;

    // Serializes GateAsync's emit+read round-trip. IServeAttachSink's wire protocol has no
    // per-request correlation ID — a response is just "the next line on the channel" — which was
    // fine when only one mutating-action gate could ever be awaiting a response at a time. That
    // stopped being true once orchestrators that run agent branches concurrently within a single
    // task (ScatterGatherOrchestrator, MapReduceOrchestrator — both via Task.WhenAll) started
    // calling GateAsync from more than one branch at once: two concurrent emit+read pairs racing
    // on the same channel could deliver one branch's answer to a different branch's pending
    // action. Holding this for the whole round-trip guarantees at most one request is ever
    // outstanding against a sink, so a response can never be misdelivered.
    private readonly SemaphoreSlim _gateLock = new(1, 1);

    /// <summary>
    /// Set once by <see cref="ServeHost"/> right after <c>OrchestratorBuilder.BuildAsync</c>
    /// returns — this service has to exist <em>before</em> that call (it's one of its own
    /// parameters, wired into the sandboxed plugins' approval closures), but the
    /// <see cref="EventEmitter"/> it logs unattended denials to is itself a product of that same
    /// call. Null until then, in which case denials simply aren't logged (there is nothing to log
    /// to yet — no task can be running before the daemon finishes starting up).
    /// </summary>
    public EventEmitter? EventEmitter { get; set; }

    /// <summary>Called by <see cref="ServeSocketListener"/> when a human connects.</summary>
    public void RegisterConnection(IServeAttachSink sink)
    {
        lock (_lock) _connections.Add(sink);
    }

    /// <summary>Called by <see cref="ServeSocketListener"/> when a connection closes.</summary>
    public void UnregisterConnection(IServeAttachSink sink)
    {
        lock (_lock) _connections.Remove(sink);
    }

    /// <summary>
    /// Called by <see cref="ServeHost"/> immediately before (and, with <c>null</c>, immediately
    /// after) running a task, so the mutating-action gates below know which attached connection —
    /// if any — dispatched the task currently in flight. Safe with no locking: fuseraft's
    /// orchestrators process exactly one task at a time (see <see cref="ServeTaskQueue"/>), so
    /// there is never more than one "current" task to set this for.
    /// </summary>
    public void SetCurrentTaskOrigin(IServeAttachSink? origin) => _currentTaskOrigin = origin;

    /// <summary>
    /// Forwards a live progress event (e.g. <c>agent_starting</c>, <c>tool_calling</c>) to every
    /// currently attached connection. Best-effort — a slow or broken connection's write failure
    /// must never disrupt the session it's merely observing.
    /// </summary>
    public void BroadcastEvent(object payload)
    {
        IServeAttachSink[] snapshot;
        lock (_lock) snapshot = [.. _connections];

        foreach (var sink in snapshot)
        {
            try { sink.Emit(payload); }
            catch { /* an observer's broken connection must not affect the running task */ }
        }
    }

    public async Task<bool> PromptShellCommandAsync(string command) =>
        await GateAsync(new { type = "approval_request", kind = "shell_command", command }, "shell_command");

    public async Task<bool> PromptToolActionAsync(string plugin, string action, string detail) =>
        await GateAsync(new { type = "approval_request", kind = "tool_action", plugin, action, detail }, "tool_action");

    public async Task<bool> PromptFileWriteAsync(string action, string path, string oldContent, string newContent) =>
        await GateAsync(new
        {
            type = "approval_request",
            kind = "file_write",
            plugin = "FileSystem",
            action,
            path,
            oldContent,
            newContent,
        }, "file_write");

    private async Task<bool> GateAsync(object requestPayload, string kind)
    {
        var origin = _currentTaskOrigin;
        if (origin is not null)
        {
            await _gateLock.WaitAsync();
            try
            {
                origin.Emit(requestPayload);
                return await origin.ReadApprovalResponseAsync();
            }
            catch (Exception)
            {
                // The originating connection died mid-approval (e.g. the client's terminal was
                // killed) and can never answer — fall through to the unattended policy exactly as
                // if there had been no origin at all, rather than letting a broken-pipe exception
                // from Emit propagate up and fail the whole task.
                UnregisterConnection(origin);
            }
            finally
            {
                _gateLock.Release();
            }
        }

        if (!allowWhenUnattended)
            _ = (EventEmitter?.EmitAsync(EventTypes.HitlRejected,
                payload: new { reason = "unattended", policy = "deny", kind }) ?? Task.CompletedTask);

        return allowWhenUnattended;
    }

    // Unreachable from this surface today — see class doc comment.
    public Task<string?> PromptContinueAsync() => Task.FromResult<string?>(null);

    public Task<string?> PromptRedirectAsync(string agentName) => Task.FromResult<string?>(null);

    public Task<string?> PromptValidatorStuckAsync(
        string agentName, string validatorName, int consecutiveFailures, string lastError) =>
        Task.FromResult<string?>(null);

    public Task<string?> PromptBlockerResolutionAsync(string agentName, string blockerMessage) =>
        Task.FromResult<string?>(null);

    public async Task<bool> PromptRouteApprovalAsync(string keyword, string sourceAgent, string targetAgent) =>
        await GateAsync(new
        {
            type = "approval_request",
            kind = "route_approval",
            keyword,
            sourceAgent,
            targetAgent,
        }, "route_approval");

    public Task<string?> PromptPostSessionAsync() => Task.FromResult<string?>(null);

    public Task<string?> PromptPlanReviewAsync(string planText) => Task.FromResult<string?>(null);
}
