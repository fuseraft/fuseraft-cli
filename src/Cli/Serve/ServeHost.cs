using System.Diagnostics;
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

            await WorkerLoopAsync(queue, built, approvalService, objectiveManager, shutdownCts.Token);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            socketListener?.Stop();
            if (mcpApp is not null) await mcpApp.DisposeAsync();
            if (built is not null)
            {
                await built.McpManager.DisposeAsync();
                built.GovernanceKernel.Dispose();
                built.ChatClientFactory.Dispose();
            }
            ReleasePidFile(pidFilePath);
        }
    }

    // -------------------------------------------------------------------------
    // Worker loop — one task at a time; blocking on an empty queue is the idle mode.
    // -------------------------------------------------------------------------

    private async Task WorkerLoopAsync(
        ServeTaskQueue queue,
        OrchestratorBuildResult built,
        ServeHumanApprovalService approvalService,
        ObjectiveManager objectiveManager,
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
                if (!queue.Reader.TryRead(out var request))
                {
                    // Queue's empty right now — see if there's auto-dispatch work to self-enqueue
                    // before falling through to the real blocking wait. This is the daemon's own
                    // initiative: it becomes an ordinary queued request from here on, so every
                    // existing mechanism (broadcast, queue position, get_status/get_result,
                    // checkpointing) applies to it with zero special-casing below.
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
                        continue;
                    }

                    request = await queue.Reader.ReadAsync(cancellationToken);
                }

                try
                {
                    await ProcessTaskAsync(request, queue, built, approvalService, objectiveManager, modelIdByAgent, cancellationToken);
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
        IReadOnlyDictionary<string, string> modelIdByAgent,
        CancellationToken cancellationToken)
    {
        var record = queue.TryGet(request.SessionId)!;
        record.Status = ServeTaskStatus.Running;

        if (request.ObjectiveId is not null)
        {
            try { await objectiveManager.LinkTaskAsync(request.ObjectiveId, request.Task, completed: false, sessionId: request.SessionId, cancellationToken); }
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
        built.Orchestrator.SetStructuredTask(TaskModel.FromGoal(request.Task));

        var runner = new SessionRunner(
            built.Orchestrator, built.Compactor, sessionStore, approvalService,
            built.EventEmitter, telemetry: null, modelIdByAgent,
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
            await sessionStore.SaveAsync(checkpoint, CancellationToken.None);
        }

        if (request.ObjectiveId is not null)
        {
            try { await objectiveManager.LinkTaskAsync(request.ObjectiveId, request.Task, completed: result.Succeeded, sessionId: request.SessionId, CancellationToken.None); }
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
