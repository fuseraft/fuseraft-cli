using System.Diagnostics;
using AgentGovernance.Audit;
using AgentGovernance.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Spectre.Console;
using fuseraft.Core;
using fuseraft.Core.Events;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models.Agents;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure.Objectives;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Serve;

/// <summary>
/// Owns the whole lifetime of a <c>fuseraft serve</c> daemon: builds the orchestrator once,
/// starts the MCP (HTTP) and attach (Unix socket) front doors, runs the single-task-at-a-time
/// worker loop that <em>is</em> the idle mode, and tears everything down on Ctrl+C.
///
/// <para>
/// One daemon = one working directory = one orchestration config = one shared orchestrator
/// instance, reused across every dispatched task (rebuilding it per task would respawn every
/// configured MCP server's child process and reset the governance kernel's circuit
/// breaker/SLO/audit state — see <c>OrchestratorBuilder.BuildAsync</c>'s doc comment). Tasks are
/// processed one at a time via <see cref="ServeTaskQueue"/> because every fuseraft orchestrator
/// assumes one shared, mutable conversation history.
/// </para>
/// </summary>
public sealed class ServeHost(
    ILoggerFactory loggerFactory,
    PluginRegistry pluginRegistry,
    ISessionStore sessionStore,
    string configPath,
    int httpPort,
    string? socketPathOverride,
    bool unattendedAllow,
    IReadOnlyList<string> autoObjectiveIds)
{
    private const int AutoDispatchFailureThreshold = 3;

    private readonly ILogger<ServeHost> _logger = loggerFactory.CreateLogger<ServeHost>();

    // Daemon-lifetime only, deliberately not persisted to the Objective/YAML schema — see
    // TryPickAutoDispatchTaskAsync's doc comment for why a failing task must stop being
    // auto-retried without being silently removed from RemainingTasks.
    private readonly Dictionary<(string ObjectiveId, string Task), int> _autoDispatchFailures = [];

    public async Task<int> RunAsync()
    {
        var projectSlug = FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory());
        var socketPath  = socketPathOverride ?? FuseraftPaths.DaemonSocketPath(projectSlug);
        var pidFilePath = FuseraftPaths.ExpandProjectPaths(FuseraftPaths.LocalDaemonPidFile, projectSlug);

        AcquirePidFile(pidFilePath);

        OrchestratorBuildResult? built = null;
        WebApplication? mcpApp = null;
        ServeSocketListener? socketListener = null;
        fuseraft.Cli.Telemetry.FuseraftTelemetry? telemetry = null;
        using var shutdownCts = new CancellationTokenSource();

        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            AnsiConsole.MarkupLine("\n[dim]Shutting down...[/]");
            shutdownCts.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            var approvalService = new ServeHumanApprovalService(unattendedAllow);

            // hitlMode: true unconditionally — this is what wires the mutating-action approval
            // closures (shell/tool-action/file-write) into the sandboxed plugins at all (see
            // OrchestratorBuilder.ResolveSecurityConfig). Without it, plugins would run
            // completely ungated regardless of what ServeHumanApprovalService decides.
            // SessionRunner's own per-call `hitlMode` (the "pause after every turn" loop) is a
            // separate, coarser flag we deliberately always pass false for below — a dispatched
            // task should run straight through, not stop after every message waiting on a
            // "continue?" prompt nobody may be present to answer.
            built = await OrchestratorBuilder.BuildAsync(
                configPath, loggerFactory, pluginRegistry,
                humanApprovalService: approvalService,
                hitlMode: true);
            approvalService.EventEmitter = built.EventEmitter;

            // One telemetry instance for the daemon's whole lifetime, exactly like the
            // orchestrator itself — OTel's exporters are meant to live for a process's duration,
            // not be spun up and torn down per dispatched task, and every counter/histogram call
            // already tags itself with the current session ID at the call site.
            telemetry = fuseraft.Cli.Telemetry.FuseraftTelemetry.Create(built.Config.Telemetry, built.Config.Name);

            var queue = new ServeTaskQueue();
            var objectiveManager = new ObjectiveManager(new ObjectiveStore(FuseraftPaths.LocalObjectives));

            mcpApp = BuildMcpApp(queue, httpPort);
            await mcpApp.StartAsync(shutdownCts.Token);
            var boundPort = new Uri(mcpApp.Urls.First()).Port;

            socketListener = new ServeSocketListener(socketPath, queue, approvalService,
                loggerFactory.CreateLogger<ServeSocketListener>());
            socketListener.Start(shutdownCts.Token);

            AnsiConsole.MarkupLine($"[dim]fuseraft serve[/] — [bold]{Markup.Escape(built.Config.Name)}[/]");
            AnsiConsole.MarkupLine($"[dim]  MCP (dispatch_task/get_status/get_result) → http://localhost:{boundPort}/mcp[/]");
            AnsiConsole.MarkupLine($"[dim]  Attach socket → {Markup.Escape(socketPath)}  (fuseraft attach)[/]");
            AnsiConsole.MarkupLine($"[dim]  Unattended mutating actions: {(unattendedAllow ? "allowed" : "denied")} (--unattended-policy)[/]");
            AnsiConsole.MarkupLine(autoObjectiveIds.Count > 0
                ? $"[dim]  Auto-dispatch → {Markup.Escape(string.Join(", ", autoObjectiveIds))} (picks its own next task when idle)[/]"
                : "[dim]  Auto-dispatch: off (--auto-objective)[/]");
            AnsiConsole.MarkupLine("[dim]  Idle — waiting for a task. Ctrl+C to stop.[/]");

            await WorkerLoopAsync(queue, built, approvalService, objectiveManager, telemetry, shutdownCts.Token);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;

            // Each teardown step is independent best-effort cleanup — one throwing (e.g. an MCP
            // server's child process refusing to terminate cleanly) must never skip the rest,
            // especially ReleasePidFile: skipping it leaks the pidfile and blocks every future
            // `fuseraft serve` invocation for this project.
            TryCleanup(() => socketListener?.Stop());
            if (mcpApp is not null) await TryCleanupAsync(() => mcpApp.DisposeAsync().AsTask());
            if (built is not null)
            {
                await TryCleanupAsync(() => built.McpManager.DisposeAsync().AsTask());
                TryCleanup(built.GovernanceKernel.Dispose);
                TryCleanup(built.ChatClientFactory.Dispose);
            }
            TryCleanup(() => telemetry?.Dispose());
            ReleasePidFile(pidFilePath);
        }
    }

    private void TryCleanup(Action action)
    {
        try { action(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error during serve daemon shutdown"); }
    }

    private async Task TryCleanupAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error during serve daemon shutdown"); }
    }

    // -------------------------------------------------------------------------
    // Worker loop — one task at a time; blocking on an empty queue is the idle mode.
    // -------------------------------------------------------------------------

    private async Task WorkerLoopAsync(
        ServeTaskQueue queue,
        OrchestratorBuildResult built,
        ServeHumanApprovalService approvalService,
        ObjectiveManager objectiveManager,
        fuseraft.Cli.Telemetry.FuseraftTelemetry? telemetry,
        CancellationToken cancellationToken)
    {
        var modelIdByAgent = built.Config.Agents.ToDictionary(
            a => a.Name,
            a => string.IsNullOrWhiteSpace(a.Model.ModelId) ? "unknown" : a.Model.ModelId,
            StringComparer.OrdinalIgnoreCase);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await NextRequestAsync(queue, built, approvalService, objectiveManager, cancellationToken);
                if (request is null) continue; // nothing to process this tick — see NextRequestAsync

                try
                {
                    await ProcessTaskAsync(request, queue, built, approvalService, objectiveManager, telemetry, modelIdByAgent, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One task's unexpected failure must never take the daemon down — it stays
                    // idle and ready for the next dispatch.
                    _logger.LogError(ex, "Task {SessionId} failed unexpectedly", request.SessionId);
                    var record = queue.TryGet(request.SessionId);
                    if (record is not null)
                    {
                        record.Status = ServeTaskStatus.Failed;
                        record.Succeeded = false;
                        record.ErrorMessage = ex.Message;
                        record.CompletedAt = DateTimeOffset.UtcNow;
                        record.Done.TrySetResult(true);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    /// <summary>
    /// Returns the next task to process, or <c>null</c> if this idle tick had nothing to do
    /// (either an auto-dispatch pick was just self-enqueued — it'll be picked up as an ordinary
    /// request on the very next tick — or picking/enqueueing it failed unexpectedly). Isolating
    /// this in its own method keeps that failure's try/catch from ever sharing a scope with
    /// <c>ProcessTaskAsync</c>'s — a corrupted objective file must never take the whole daemon
    /// down, but it also must never be confused for a specific task's failure, since no task was
    /// ever picked or dispatched.
    /// </summary>
    private async Task<ServeTaskRequest?> NextRequestAsync(
        ServeTaskQueue queue,
        OrchestratorBuildResult built,
        ServeHumanApprovalService approvalService,
        ObjectiveManager objectiveManager,
        CancellationToken cancellationToken)
    {
        if (queue.Reader.TryRead(out var request))
            return request;

        try
        {
            // Queue's empty right now — see if there's auto-dispatch work to self-enqueue before
            // falling through to the real blocking wait. This is the daemon's own initiative: it
            // becomes an ordinary queued request from here on, so every existing mechanism
            // (broadcast, queue position, get_status/get_result, checkpointing) applies to it
            // with zero special-casing below.
            var picked = autoObjectiveIds.Count > 0
                ? await TryPickAutoDispatchTaskAsync(objectiveManager, cancellationToken)
                : null;

            if (picked is { } p)
            {
                var sessionId = queue.Enqueue(p.Task, p.ObjectiveId);
                approvalService.BroadcastEvent(new
                {
                    type = "auto_dispatched",
                    sessionId,
                    objectiveId = p.ObjectiveId,
                    task = p.Task,
                });
                _ = (built.EventEmitter?.EmitAsync(EventTypes.AutoDispatch,
                    payload: new { session = sessionId, objective = p.ObjectiveId }) ?? Task.CompletedTask);
                return null;
            }

            return await queue.Reader.ReadAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Used to run outside any try/catch here and would take the whole daemon down,
            // contradicting WorkerLoopAsync's own "one task's failure must never take the daemon
            // down" guarantee. Log and retry on the next idle tick.
            _logger.LogError(ex, "Auto-dispatch pick failed unexpectedly");
            return null;
        }
    }

    /// <summary>
    /// Picks the first <c>RemainingTasks</c> entry, across the configured auto-objectives in
    /// order, that hasn't hit <see cref="AutoDispatchFailureThreshold"/> consecutive failures.
    /// Without this, a task that keeps failing would sit right back in <c>RemainingTasks</c>
    /// after every attempt (see <c>ProcessTaskAsync</c>'s <c>LinkTaskAsync(completed: false, ...)</c>
    /// call) and get immediately re-picked on the next idle tick forever. The failed task stays
    /// visible in <c>RemainingTasks</c> either way — a human can inspect, fix, or explicitly
    /// re-dispatch it — this only stops the daemon from silently burning API calls retrying it
    /// unattended.
    /// </summary>
    private async Task<(string ObjectiveId, string Task)?> TryPickAutoDispatchTaskAsync(
        ObjectiveManager objectiveManager, CancellationToken cancellationToken)
    {
        foreach (var objectiveId in autoObjectiveIds)
        {
            var objective = await objectiveManager.GetAsync(objectiveId, cancellationToken);
            if (objective is null || objective.Status != "Active") continue;

            foreach (var task in objective.RemainingTasks)
            {
                if (_autoDispatchFailures.GetValueOrDefault((objectiveId, task)) < AutoDispatchFailureThreshold)
                    return (objectiveId, task);
            }
        }
        return null;
    }

    private async Task ProcessTaskAsync(
        ServeTaskRequest request,
        ServeTaskQueue queue,
        OrchestratorBuildResult built,
        ServeHumanApprovalService approvalService,
        ObjectiveManager objectiveManager,
        fuseraft.Cli.Telemetry.FuseraftTelemetry? telemetry,
        IReadOnlyDictionary<string, string> modelIdByAgent,
        CancellationToken cancellationToken)
    {
        var record = queue.TryGet(request.SessionId)!;
        record.Status = ServeTaskStatus.Running;

        // Screen dispatched task text for prompt injection exactly like RunCommand does for a
        // new session's task — relevant here specifically because `dispatch_task` is an
        // MCP-exposed entry point another, potentially adversarial agent can call, unlike a
        // human typing a task at their own REPL prompt.
        if (built.GovernanceKernel.InjectionDetector is { } detector)
        {
            var detection = detector.Detect(request.Task);
            if (detection.IsInjection && detection.ThreatLevel >= ThreatLevel.High)
            {
                built.GovernanceKernel.AuditEmitter.Emit(
                    GovernanceEventType.ToolCallBlocked,
                    agentId:   "did:fuseraft:task-input",
                    sessionId: request.SessionId,
                    data:      new Dictionary<string, object>
                    {
                        ["injection_type"] = detection.InjectionType.ToString(),
                        ["threat_level"]   = detection.ThreatLevel.ToString(),
                        ["confidence"]     = detection.Confidence,
                        ["input_hash"]     = detection.InputHash ?? string.Empty,
                    });

                record.Status       = ServeTaskStatus.Failed;
                record.Succeeded    = false;
                record.ErrorMessage = $"Task rejected: prompt injection detected ({detection.InjectionType}, confidence {detection.Confidence:P0}).";
                record.CompletedAt  = DateTimeOffset.UtcNow;
                record.Done.TrySetResult(true);
                return;
            }
        }

        if (request.ObjectiveId is not null)
        {
            // addIfMissing: false — a task dispatched (by a human or an MCP caller) under an
            // objective ID it wasn't actually planned under must not silently expand that
            // objective's RemainingTasks; this call should only ever affirm/no-op on a task the
            // objective already knows about (e.g. one TryPickAutoDispatchTaskAsync picked).
            try { await objectiveManager.LinkTaskAsync(request.ObjectiveId, request.Task, completed: false, sessionId: request.SessionId, cancellationToken, addIfMissing: false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not link task start to objective {ObjectiveId}", request.ObjectiveId); }
        }

        var checkpoint = new SessionCheckpoint
        {
            SessionId        = request.SessionId,
            Task             = request.Task,
            ConfigPath       = configPath,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        // Seed save so this session shows up in `fuseraft sessions` even if the process is
        // killed before the first turn completes — mirrors RunCommand's own seed save.
        await sessionStore.SaveAsync(checkpoint, cancellationToken);

        if (built.ChangeTracker is not null)
            await built.ChangeTracker.SetSessionIdAsync(request.SessionId, cancellationToken);
        built.EventEmitter?.SetSessionId(request.SessionId);
        built.Orchestrator.SetSessionId(request.SessionId);
        built.Compactor?.SetSessionId(request.SessionId);
        // Re-scopes SessionReadCache/ToolResultArtifactStore/SessionContextPlugin to this task's
        // session — they were all built once, bound to whatever sessionId (none) was in scope at
        // daemon startup, and would otherwise stay frozen there for every task the daemon ever
        // runs. See OrchestratorBuilder's RebindSessionScopedState doc comment.
        built.RebindSessionScopedState?.Invoke(request.SessionId);
        built.Orchestrator.SetStructuredTask(TaskModel.FromGoal(request.Task));

        var runner = new SessionRunner(
            built.Orchestrator, built.Compactor, sessionStore, approvalService,
            built.EventEmitter, telemetry, modelIdByAgent,
            devUI: null, configPath: configPath,
            maxIterations: built.Config.Termination?.ResolveMaxIterations() ?? 0,
            contextBudget: built.Config.ContextBudget,
            quiet: true);

        // Live progress broadcast to every attached connection, and approval scoped to this
        // task's own originator (if any) — added/removed per task, not once at startup, since
        // the same orchestrator instance is reused across every dispatched task for the daemon's
        // whole lifetime; leaving these attached would multiply on every subsequent task.
        void OnAgentStarting(string agent) =>
            approvalService.BroadcastEvent(new { type = "agent_starting", agent });
        void OnToolCalling(string agent, string tool, string? argsSummary) =>
            approvalService.BroadcastEvent(new { type = "tool_calling", agent, tool, argsSummary });
        void OnTokenBudgetWarning(string agent, int inputTokens, int warnThreshold) =>
            approvalService.BroadcastEvent(new { type = "token_budget_warning", agent, inputTokens, warnThreshold });

        built.Orchestrator.AgentStarting      += OnAgentStarting;
        built.Orchestrator.ToolCalling        += OnToolCalling;
        built.Orchestrator.TokenBudgetWarning += OnTokenBudgetWarning;
        approvalService.SetCurrentTaskOrigin(request.OriginSink);

        SessionResult result;
        try
        {
            // hitlMode: false — see the comment on hitlMode in RunAsync above. A dispatched task
            // runs straight through; mutating actions are still gated via ServeHumanApprovalService
            // regardless of this flag.
            result = await runner.RunAsync(request.Task, checkpoint, hitlMode: false, showTools: false, cancellationToken);
        }
        catch (Exception ex)
        {
            result = new SessionResult(false, ex.Message, checkpoint.Messages, TimeSpan.Zero);
        }
        finally
        {
            approvalService.SetCurrentTaskOrigin(null);
            built.Orchestrator.AgentStarting      -= OnAgentStarting;
            built.Orchestrator.ToolCalling        -= OnToolCalling;
            built.Orchestrator.TokenBudgetWarning -= OnTokenBudgetWarning;
        }

        var last = result.Messages.LastOrDefault();
        record.TurnCount          = result.Messages.Count;
        record.LastAgent          = last?.AgentName;
        record.LastMessageExcerpt = Truncate(last?.Content);
        record.Messages           = result.Messages;
        record.Succeeded          = result.Succeeded;
        record.ErrorMessage       = result.ErrorMessage;
        record.Status             = result.Succeeded ? ServeTaskStatus.Completed : ServeTaskStatus.Failed;
        record.CompletedAt        = DateTimeOffset.UtcNow;

        if (result.Succeeded)
        {
            checkpoint.IsComplete = true;
            try
            {
                await sessionStore.SaveAsync(checkpoint, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The task itself already succeeded — record.Status/Succeeded above already
                // reflect that. A failure to persist the final checkpoint must not propagate out
                // of here: WorkerLoopAsync's catch-all would otherwise flip record.Status back to
                // Failed while record.Succeeded stayed true (an inconsistent result for
                // get_result to report) and skip the objective-completion bookkeeping and
                // failure-counter update below, letting --auto-objective retry a task that
                // actually succeeded forever.
                _logger.LogWarning(ex, "Could not persist final checkpoint for session {SessionId}", request.SessionId);
            }
        }

        if (request.ObjectiveId is not null)
        {
            try { await objectiveManager.LinkTaskAsync(request.ObjectiveId, request.Task, completed: result.Succeeded, sessionId: request.SessionId, CancellationToken.None, addIfMissing: false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not link task completion to objective {ObjectiveId}", request.ObjectiveId); }

            // Tracks whether the auto-dispatch picker should keep offering this task again —
            // see TryPickAutoDispatchTaskAsync. Applies regardless of dispatch origin: a task a
            // human explicitly ran under an objective ID is just as much "already tried and
            // failing" as one the daemon picked itself.
            var failureKey = (request.ObjectiveId, request.Task);
            if (result.Succeeded)
                _autoDispatchFailures.Remove(failureKey);
            else
                _autoDispatchFailures[failureKey] = _autoDispatchFailures.GetValueOrDefault(failureKey) + 1;
        }

        // Post-task skill curation and repository memory extraction — mirrors RunCommand's own
        // post-session steps exactly (best-effort, never fails the task). Without this, a
        // project with SkillCuration.Enabled: true gets skills curated after every `fuseraft
        // run` session but silently none after a `fuseraft serve`-dispatched task, with nothing
        // logged to indicate the feature is inactive on this path.
        if (built.SkillCurator is not null && result.Succeeded)
        {
            try
            {
                _ = (built.EventEmitter?.EmitAsync(EventTypes.SkillCurationStart,
                    payload: new { session = request.SessionId, source = "serve" }) ?? Task.CompletedTask);

                var curationResult = await built.SkillCurator.RunAsync(
                    checkpoint, result.Messages, CancellationToken.None, source: "serve");

                _ = (built.EventEmitter?.EmitAsync(EventTypes.SkillCurationComplete,
                    payload: new
                    {
                        session        = request.SessionId,
                        source         = "serve",
                        outcome        = curationResult.Outcome.ToString().ToLowerInvariant(),
                        slug           = curationResult.Slug,
                        path           = curationResult.Path,
                        turns_digested = curationResult.TurnsDigested,
                        failure_reason = curationResult.FailureReason,
                    }) ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skill curation failed for session {SessionId}", request.SessionId);
            }
        }

        if (built.RepositoryMemoryExtractor is not null && result.Succeeded)
        {
            try { await built.RepositoryMemoryExtractor.ExtractAsync(sessionId: request.SessionId, CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Repository memory extraction failed for session {SessionId}", request.SessionId); }
        }

        record.Done.TrySetResult(true);
    }

    private static string? Truncate(string? content) =>
        content is { Length: > 500 } ? content[..500] + "…" : content;

    // -------------------------------------------------------------------------
    // MCP HTTP host
    // -------------------------------------------------------------------------

    private static WebApplication BuildMcpApp(ServeTaskQueue queue, int port)
    {
        var mcpTools = new ServeMcpTools(queue);
        var toolNames = new (string Method, string ToolName)[]
        {
            (nameof(ServeMcpTools.DispatchTask), "dispatch_task"),
            (nameof(ServeMcpTools.GetStatus),    "get_status"),
            (nameof(ServeMcpTools.GetResult),    "get_result"),
        };
        var tools = toolNames.Select(t =>
        {
            var method = typeof(ServeMcpTools).GetMethod(t.Method)!;
            var function = AIFunctionFactory.Create(method, mcpTools, new AIFunctionFactoryOptions { Name = t.ToolName });
            return McpServerTool.Create(function);
        });

        var builder = WebApplication.CreateSlimBuilder([]);
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools(tools);

        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    // -------------------------------------------------------------------------
    // Single-instance guard
    // -------------------------------------------------------------------------

    private static void AcquirePidFile(string pidFilePath)
    {
        var dir = Path.GetDirectoryName(pidFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(pidFilePath) &&
            int.TryParse(File.ReadAllText(pidFilePath).Trim(), out var existingPid) &&
            IsProcessAlive(existingPid))
        {
            throw new InvalidOperationException(
                $"fuseraft serve is already running for this project (pid {existingPid}, pidfile {pidFilePath}). " +
                "Stop it first, or delete the pidfile if it's stale.");
        }

        File.WriteAllText(pidFilePath, Environment.ProcessId.ToString());
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void ReleasePidFile(string pidFilePath)
    {
        try { if (File.Exists(pidFilePath)) File.Delete(pidFilePath); }
        catch { /* best-effort */ }
    }
}
