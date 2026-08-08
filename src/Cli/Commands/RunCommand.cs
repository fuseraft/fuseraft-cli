using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentGovernance.Audit;
using AgentGovernance.Security;
using AgentGovernance.Sre;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.DevUI;
using fuseraft.Cli.Display;
using fuseraft.Cli.Telemetry;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;
using MagenticOrchestrator = fuseraft.Orchestration.MagenticOrchestrator;

namespace fuseraft.Cli.Commands;

public sealed class RunSettings : CommandSettings
{
    [CommandArgument(0, "[task]")]
    [Description("Task for the agent team. Omit to be prompted interactively.")]
    public string? Task { get; set; }

    [CommandOption("-c|--config")]
    [Description("Path to orchestration config YAML or JSON (default: config/orchestration.yaml).")]
    public string? ConfigPath { get; set; }

    [CommandOption("-r|--resume")]
    [Description("Resume an incomplete session by its ID (e.g. --resume a1b2c3d4). Omit the ID to be prompted.")]
    public string? Resume { get; set; }

    [CommandOption("--hitl")]
    [Description("Human-in-the-loop mode: pause after each agent turn so you can redirect or stop the session.")]
    public bool HumanInTheLoop { get; set; }

    [CommandOption("-o|--output")]
    [Description("Save the full session transcript to a file.")]
    public string? OutputPath { get; set; }

    [CommandOption("--verbose")]
    [Description("Show debug-level log output.")]
    public bool Verbose { get; set; }

    [CommandOption("-f|--task-file")]
    [Description("Read the task from a file instead of the command line. Useful for long or multi-line tasks. Ignored when resuming a session.")]
    public string? TaskFile { get; set; }

    [CommandOption("--tools")]
    [Description("Show tool calls made by each agent inline in the turn panel.")]
    public bool ShowTools { get; set; }

    [CommandOption("--no-banner")]
    [Description("Skip the startup banner (useful in CI / piped output).")]
    public bool NoBanner { get; set; }

    [CommandOption("--ci")]
    [Description("CI mode: exit 2 if any acceptance criterion in test-report.json is FAIL after the session completes.")]
    public bool Ci { get; set; }

    [CommandOption("--devui")]
    [Description("Start a local web server for real-time session visualization (prints URL on startup).")]
    public bool DevUI { get; set; }

    [CommandOption("--work-dir")]
    [Description("Set the working directory for this session. Falls back to the sandbox path in the config, or the current directory if neither is set.")]
    public string? WorkDir { get; set; }

    [CommandOption("--context-file")]
    [Description("Attach a file as context — its content is appended to the task. Repeatable.")]
    public string[]? ContextFiles { get; set; }

    [CommandOption("--spec")]
    [Description("Path to a spec file (Markdown, plain text, or JSON) that anchors all agents to an agreed specification. Agents treat it as the authoritative source of truth.")]
    public string? SpecFile { get; set; }

    [CommandOption("--no-replan")]
    [Description("Disable replanning: strip any state-machine transitions whose signal contains 'REPLAN' so the session cannot route back to the planning phase mid-run.")]
    public bool NoReplan { get; set; }

    [CommandOption("--snapshot")]
    [Description("Capture per-turn postmortem snapshots to ~/.fuseraft/snapshots/<project>/<session>/. Writes turns.jsonl (agent messages + tool calls) and manifest.json (run summary).")]
    public bool Snapshot { get; set; }

    [CommandOption("--json")]
    [Description("Suppress interactive console output (banner, turn panels, spinner) — human-readable status still goes to stderr — and print one JSON summary object to stdout when the session ends. Same effect as Output.Json: true in the config; this flag always wins.")]
    public bool Json { get; set; }
}

/// <summary>
/// Default command — runs (or resumes) an orchestration session.
/// Supports human-in-the-loop mode where the user can redirect the conversation
/// between agent turns.
/// </summary>
public sealed class RunCommand(ILoggerFactory loggerFactory, PluginRegistry pluginRegistry, ISessionStore sessionStore)
    : AsyncCommand<RunSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, RunSettings settings, CancellationToken cancellationToken)
    {
        // --json redirects all human-readable Spectre output to stderr so stdout stays a clean
        // channel for the single JSON summary object printed at the end of the run. The config
        // file can also enable this (Output.Json: true), but that isn't known until after the
        // config loads below.
        //
        // Every diagnostic printed before the config loads (work-dir resolution, resume lookup,
        // spec loading, and a config load failure itself) therefore always renders through
        // StderrConsole below — never the ambient AnsiConsole.Console — regardless of whether
        // jsonMode ends up true. That guarantees stdout can never receive stray text ahead of the
        // JSON summary, in either the --json or the config-only Output.Json case. Additionally,
        // whenever settings.Json (the CLI flag) is set, jsonMode is already known true up front,
        // so these early-return paths also emit a minimal JSON error summary via
        // EmitJsonErrorIfNeeded — a script driving fuseraft with --json gets exactly one JSON
        // line on stdout even when the run fails before a session ever starts.
        //
        // Every early-return path *after* the config loads (API key validation, task-file
        // resolution, prompt-injection rejection, a failed/cancelled pre-loop compaction) is keyed
        // on jsonMode instead of settings.Json, since jsonMode is fully resolved by then — so a
        // config-only Output.Json: true run gets the same one-JSON-line-or-nothing guarantee as
        // --json for every failure past that point. The one case that can't be closed: config-only
        // Output.Json with a failure before the config finishes loading — Output.Json genuinely
        // can't be read from a config that hasn't loaded yet, so that path falls back to
        // exit-code-only signalling (stdout stays empty, never wrong).
        if (settings.Json)
            RedirectAnsiConsoleToStderr();

        // Determine the config path early so we can build the right session store before
        // loading the full config. When resuming, checkpoint.ConfigPath will refine this later.
        // Resolve to absolute immediately so it stays valid after a potential CWD change below.
        var configPath = Path.GetFullPath(settings.ConfigPath ?? ".fuseraft/config/orchestration.yaml");

        // Resolve the effective working directory: --work-dir > config sandbox path > CWD.
        // This must happen before BuildActiveStore so that all subsequent relative-path
        // resolutions (checkpoint path, validation paths, change log, etc.) are rooted here.
        var workDir = ResolveWorkDir(settings.WorkDir, configPath, loggerFactory.CreateLogger<RunCommand>());
        if (workDir is not null)
        {
            if (!Directory.Exists(workDir))
            {
                StderrConsole.MarkupLine($"[red]✗ Work directory not found:[/] {Markup.Escape(workDir)}");
                EmitJsonErrorIfNeeded(settings.Json, configPath, $"Work directory not found: {workDir}", 1);
                return 1;
            }
            Directory.SetCurrentDirectory(workDir);
            StderrConsole.MarkupLine($"[dim]Working directory → {Markup.Escape(workDir)}[/]");
        }

        // Build the active session store from the checkpoint config in the config file.
        // Falls back to the global injected store when no Checkpoint section is present.
        var activeStore = BuildActiveStore(configPath, loggerFactory, sessionStore);

        // Resolve session to resume (if requested)
        SessionCheckpoint? checkpoint = null;

        if (settings.Resume is not null)
        {
            if (activeStore is InMemorySessionStore)
            {
                StderrConsole.MarkupLine("[yellow]⚠ CheckpointMode is 'memory' — sessions are not persisted and cannot be resumed.[/]");
                EmitJsonErrorIfNeeded(settings.Json, configPath, "CheckpointMode is 'memory' — sessions are not persisted and cannot be resumed.", 1);
                return 1;
            }

            // Try the active (project-local) store first; fall back to the global store so
            // sessions created before a CheckpointPath was configured can still be resumed.
            checkpoint = await ResolveCheckpointAsync(settings.Resume, activeStore);
            if (checkpoint is null && !ReferenceEquals(activeStore, sessionStore))
                checkpoint = await ResolveCheckpointAsync(settings.Resume, sessionStore);
            if (checkpoint is null)
            {
                // ResolveCheckpointAsync already printed the specific reason (not found /
                // already complete) via StderrConsole.
                EmitJsonErrorIfNeeded(settings.Json, configPath, $"Could not resolve session to resume: {settings.Resume}", 1);
                return 1;
            }

            // TurnIndex of the last message equals the highest turn number, accounting for
            // any previous compactions where Messages.Count < total turns elapsed.
            var turnsComplete = checkpoint.Messages.Count > 0
                ? checkpoint.Messages[^1].TurnIndex + 1
                : 0;

            StderrConsole.MarkupLine($"[dim]Resuming session [bold]{checkpoint.SessionId}[/] " +
                                      $"({turnsComplete} turns already complete)[/]");
        }

        // Reconcile config path: an existing checkpoint always knows its own config.
        configPath = checkpoint?.ConfigPath ?? configPath;

        // Pre-generate session ID so the startup header can show a stable value even
        // before the checkpoint object is constructed (which requires the task string).
        var pendingSessionId = checkpoint?.SessionId ?? Guid.NewGuid().ToString("N")[..8];

        // Load spec file (--spec) before building so the content can be injected into
        // every agent's system prompt as the authoritative specification.
        string? specContent = null;
        if (settings.SpecFile is not null)
        {
            var absSpec = Path.IsPathRooted(settings.SpecFile)
                ? settings.SpecFile
                : Path.GetFullPath(settings.SpecFile);
            if (!File.Exists(absSpec))
            {
                StderrConsole.MarkupLine($"[red]✗ Spec file not found:[/] {Markup.Escape(absSpec)}");
                EmitJsonErrorIfNeeded(settings.Json, configPath, $"Spec file not found: {absSpec}", 1);
                return 1;
            }
            specContent = (await File.ReadAllTextAsync(absSpec, cancellationToken)).Trim();
            if (string.IsNullOrWhiteSpace(specContent))
            {
                StderrConsole.MarkupLine($"[red]✗ Spec file is empty:[/] {Markup.Escape(absSpec)}");
                EmitJsonErrorIfNeeded(settings.Json, configPath, $"Spec file is empty: {absSpec}", 1);
                return 1;
            }
            StderrConsole.MarkupLine($"[dim]Spec → {Markup.Escape(absSpec)}[/]");
        }

        var approvalService = new ConsoleHumanApprovalService();

        OrchestratorBuildResult built;
        try
        {
            built = await OrchestratorBuilder.BuildAsync(configPath, loggerFactory, pluginRegistry, approvalService, settings.HumanInTheLoop, sessionId: pendingSessionId, specContent: specContent, noReplan: settings.NoReplan);
        }
        catch (Exception ex)
        {
            StderrConsole.MarkupLine($"[red]✗ Config error:[/] {Markup.Escape(ex.Message)}");
            EmitJsonErrorIfNeeded(settings.Json, configPath, $"Config error: {ex.Message}", 1);
            return 1;
        }

        var (orchestrator, config, mcpManager, compactor, changeTracker, eventEmitter, governanceKernel, skillCurator, repoMemoryExtractor, chatClientFactory, _, sessionMetrics) = built;

        // The config can also request JSON mode (Output.Json: true) for orchestrations that are
        // always invoked by scripts. Apply the same stderr redirect if the CLI flag didn't
        // already trigger it above.
        var jsonMode = settings.Json || config.Output?.Json == true;
        if (jsonMode && !settings.Json)
            RedirectAnsiConsoleToStderr();

        await using var _mcp = mcpManager;
        using var _governance = governanceKernel;
        using var _chatClientFactory = chatClientFactory;

        // Build a fast agent→modelId lookup for telemetry tagging.
        var modelIdByAgent = config.Agents
            .ToDictionary(
                a => a.Name,
                a => string.IsNullOrWhiteSpace(a.Model.ModelId) ? "unknown" : a.Model.ModelId,
                StringComparer.OrdinalIgnoreCase);

        using var telemetry = FuseraftTelemetry.Create(config.Telemetry, config.Name);

        if (!settings.NoBanner && !jsonMode)
        {
            var skills      = DiscoverSkills();
            var pluginNames = config.Agents
                .SelectMany(a => a.Plugins)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var modelIds = config.Agents
                .Select(a => a.Model.ModelId)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var modelDisplay = modelIds.Count > 0 ? string.Join(", ", modelIds) : "unknown";
            MessageRenderer.RenderReplHeader(
                modelDisplay,
                Directory.GetCurrentDirectory(),
                pluginNames,
                pendingSessionId,
                memoryCount: 0,
                skillCount:  skills.Count);
        }

        // Validate API keys early so a bad/missing key surfaces before the session starts.
        try
        {
            await ApiKeyValidator.ValidateApiKeysAsync(config);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ API key validation failed:[/] {Markup.Escape(ex.Message)}");
            EmitJsonErrorIfNeeded(jsonMode, configPath, $"API key validation failed: {ex.Message}", 1);
            return 1;
        }

        // Resolve task
        string? task = checkpoint?.Task;

        if (task is not null)
        {
            // Resuming — ignore any task input and warn if the user supplied one.
            if (!string.IsNullOrWhiteSpace(settings.Task) || settings.TaskFile is not null)
                AnsiConsole.MarkupLine("[yellow]⚠ Task input ignored when resuming — using the session's original task.[/]");
        }
        else if (settings.TaskFile is not null)
        {
            // Load task from file.
            if (!File.Exists(settings.TaskFile))
            {
                AnsiConsole.MarkupLine($"[red]✗ Task file not found:[/] {Markup.Escape(settings.TaskFile)}");
                EmitJsonErrorIfNeeded(jsonMode, configPath, $"Task file not found: {settings.TaskFile}", 1);
                return 1;
            }

            task = (await File.ReadAllTextAsync(settings.TaskFile)).Trim();

            if (string.IsNullOrWhiteSpace(task))
            {
                AnsiConsole.MarkupLine($"[red]✗ Task file is empty:[/] {Markup.Escape(settings.TaskFile)}");
                EmitJsonErrorIfNeeded(jsonMode, configPath, $"Task file is empty: {settings.TaskFile}", 1);
                return 1;
            }
        }
        else
        {
            task = settings.Task?.Trim();
        }

        if (string.IsNullOrEmpty(task))
        {
            if (specContent is not null)
            {
                // Spec provided with no explicit task — the spec IS the mission.
                task = "Implement the specification.";
            }
            else
            {
                task = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Task[/] [dim](Enter to use demo)[/]:")
                        .AllowEmpty());

                if (string.IsNullOrWhiteSpace(task))
                    task = DefaultDemoTask;
            }
        }

        // Append spec as an authoritative block so the Planner sees it at turn 0.
        // Only on new sessions — resumed sessions already have the spec in history.
        if (checkpoint is null && specContent is not null)
        {
            var ext = Path.GetExtension(settings.SpecFile ?? string.Empty).TrimStart('.');
            if (string.IsNullOrEmpty(ext)) ext = "txt";
            task = task.TrimEnd() +
                $"\n\n---\nSPEC (authoritative — treat this as the single source of truth; your brief.json must derive directly from it):\n```{ext}\n{specContent}\n```";
        }

        if (checkpoint is null && settings.ContextFiles is { Length: > 0 } && !string.IsNullOrWhiteSpace(task))
        {
            var sb = new System.Text.StringBuilder(task);
            sb.Append("\n\n---\nAttached files:\n");
            foreach (var contextPath in settings.ContextFiles)
            {
                if (string.IsNullOrWhiteSpace(contextPath)) { continue; }
                var absPath = Path.IsPathRooted(contextPath) ? contextPath : Path.GetFullPath(contextPath);
                if (!File.Exists(absPath))
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠ Context file not found:[/] {Markup.Escape(absPath)}");
                    continue;
                }
                try
                {
                    string content;
                    string ext;
                    if (DocumentTextExtractor.IsSupported(absPath))
                    {
                        var (text, info) = DocumentTextExtractor.Extract(absPath);
                        content = text;
                        ext     = "txt";
                        AnsiConsole.MarkupLine($"[dim]Extracted {Markup.Escape(Path.GetFileName(absPath))}: {Markup.Escape(info)}[/]");
                    }
                    else
                    {
                        content = await File.ReadAllTextAsync(absPath);
                        ext     = Path.GetExtension(absPath).TrimStart('.');
                    }
                    sb.Append($"\n### {Path.GetFileName(absPath)}\n```{ext}\n{content}\n```");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠ Could not read context file:[/] {Markup.Escape(ex.Message)}");
                }
            }
            task = sb.ToString();
        }

        MessageRenderer.RenderTask(task);

        // Scan task input for prompt injection on new sessions only (not resumes).
        if (checkpoint is null && governanceKernel.InjectionDetector is { } detector)
        {
            var detection = detector.Detect(task);
            if (detection.IsInjection && detection.ThreatLevel >= ThreatLevel.High)
            {
                governanceKernel.AuditEmitter.Emit(
                    GovernanceEventType.ToolCallBlocked,
                    agentId:   "did:fuseraft:task-input",
                    sessionId: "pre-session",
                    data:      new Dictionary<string, object>
                    {
                        ["injection_type"] = detection.InjectionType.ToString(),
                        ["threat_level"]   = detection.ThreatLevel.ToString(),
                        ["confidence"]     = detection.Confidence,
                        ["input_hash"]     = detection.InputHash ?? string.Empty,
                    });

                AnsiConsole.MarkupLine(
                    $"[red]✗ Task rejected:[/] prompt injection detected " +
                    $"([bold]{detection.InjectionType}[/], confidence {detection.Confidence:P0}).");
                EmitJsonErrorIfNeeded(jsonMode, configPath,
                    $"Task rejected: prompt injection detected ({detection.InjectionType}, confidence {detection.Confidence:P0}).", 1);
                return 1;
            }
        }

        if (settings.HumanInTheLoop)
            AnsiConsole.MarkupLine("[dim]HITL mode enabled — you will be prompted after each agent turn.[/]\n");

        // Prepare checkpoint
        var isNewSession = checkpoint is null;
        checkpoint ??= new SessionCheckpoint
        {
            SessionId        = pendingSessionId,
            Task             = task,
            ConfigPath       = configPath,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };

        // Write a seed checkpoint immediately so this session appears in the sessions list
        // even if the process dies before the first agent turn completes.
        if (isNewSession)
        {
            await activeStore.SaveAsync(checkpoint, cancellationToken);
            _ = eventEmitter?.EmitAsync(EventTypes.CheckpointCreated,
                payload: new { session = checkpoint.SessionId });
        }

        // Set up the context window recorder — appends per-turn snapshots for post-run visualization.
        var ctxSnapshotsPath = fuseraft.Core.FuseraftPaths.ExpandSessionPaths(
            fuseraft.Core.FuseraftPaths.GlobalCtxSnapshotsTemplate,
            checkpoint.SessionId,
            fuseraft.Core.FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory()));
        using var ctxRecorder = new fuseraft.Orchestration.Context.ContextWindowRecorder(ctxSnapshotsPath);
        ctxRecorder.SetSessionId(checkpoint.SessionId);

        // Postmortem snapshot writer — only active when --snapshot is passed.
        var snapshotDir = fuseraft.Core.FuseraftPaths.ExpandSessionPaths(
            fuseraft.Core.FuseraftPaths.GlobalPostmortemSnapshotTemplate,
            checkpoint.SessionId,
            fuseraft.Core.FuseraftPaths.ProjectSlug(Directory.GetCurrentDirectory()));
        using var snapshotWriter = settings.Snapshot
            ? new fuseraft.Orchestration.Tracking.SnapshotWriter(snapshotDir)
            : null;
        snapshotWriter?.SetSessionId(checkpoint.SessionId);
        if (snapshotWriter is not null)
            AnsiConsole.MarkupLine($"[dim]Snapshot → {Markup.Escape(snapshotDir)}[/]");

        // Stamp the session ID on the change tracker so check 8 in TestReportValid filters
        // to only commands recorded in this session, preventing prior-session contamination.
        if (changeTracker is not null)
            await changeTracker.SetSessionIdAsync(checkpoint.SessionId);

        // Stamp the session ID on the event emitter, orchestrator, and compactor so every
        // component that uses session-scoped paths (e.g. brief.json) resolves them correctly.
        eventEmitter?.SetSessionId(checkpoint.SessionId);
        if (activeStore is JsonSessionStore jsStore && eventEmitter is not null)
            jsStore.OnCorruptionDetected = (sid, error) =>
                eventEmitter.EmitAsync(EventTypes.EventCorruptionDetected,
                    payload: new { session = sid, source = "session_checkpoint", error });
        if (!isNewSession && eventEmitter is not null)
        {
            _ = eventEmitter.EmitAsync(EventTypes.SessionRecovered,
                payload: new
                {
                    session      = checkpoint.SessionId,
                    turns_prior  = checkpoint.Messages.Count,
                });
            _ = eventEmitter.EmitAsync(EventTypes.CheckpointLoaded,
                payload: new { session = checkpoint.SessionId, turns = checkpoint.Messages.Count });
        }
        orchestrator.SetSessionId(checkpoint.SessionId);
        compactor?.SetSessionId(checkpoint.SessionId);

        // Seed structured task model (resumed sessions may already have it in the checkpoint).
        orchestrator.SetStructuredTask(
            checkpoint.StructuredTask ?? TaskModel.FromGoal(task));

        // Compact before the stream starts if the existing history is already over the threshold.
        // This covers the resume case where a prior session accumulated too many turns. Routed
        // through the same CompactionCoordinator.TryTriggerCompactionAsync path SessionRunner
        // uses mid-loop, so a resumed session gets the same TryPinLastRoutingSignal / state
        // snapshot / CompactionResumeCandidate-event protections as one compacted mid-loop,
        // rather than a stripped-down duplicate of that logic.
        if (compactor?.ShouldCompact(checkpoint.Messages) == true)
        {
            var preLoopBudgetManager = new ContextBudgetManager(contextBudget: null, contextWindowRecorder: ctxRecorder, eventEmitter: eventEmitter);
            var preLoopCoordinator = new CompactionCoordinator(
                orchestrator, compactor, activeStore, eventEmitter, sessionMetrics, ctxRecorder,
                sessionId =>
                {
                    if (!string.IsNullOrEmpty(configPath))
                    {
                        var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), configPath);
                        return $"fuseraft run --config {rel} --resume {sessionId}";
                    }
                    return $"fuseraft run --resume {sessionId}";
                });

            var totalAssistantTurnsSoFar = checkpoint.Messages.Count(m => m.Role == MessageRole.Assistant);
            var (updatedCheckpoint, shouldBreak, _, _) = await preLoopCoordinator.TryTriggerCompactionAsync(
                task, checkpoint, totalAssistantTurnsSoFar, preLoopBudgetManager, cancellationToken);
            checkpoint = updatedCheckpoint;

            // TryTriggerCompactionAsync already prints its own cancellation/failure message
            // (including the resume hint) before returning shouldBreak — nothing more to log here.
            if (shouldBreak)
            {
                EmitJsonErrorIfNeeded(jsonMode, configPath,
                    "Session could not resume: history compaction was cancelled or failed before the run could start.", 1);
                return 1;
            }

            AnsiConsole.MarkupLine("[dim]History compacted before resuming.[/]");
        }

        // Inject relevant skills from the index into fresh sessions (not resumptions).
        // This tells agents which skills are most applicable to the current task so they
        // can invoke them without scanning the full library.
        if (config.SkillCuration?.Enabled == true && checkpoint.Messages.Count == 0)
            await InjectSkillContextAsync(task, config.SkillCuration, checkpoint, cancellationToken);

        // If the checkpoint carries an explicit resume executor hint (written before a prior
        // compaction or crash), push it to the orchestrator so the first StreamAsync starts
        // from the correct agent.  This covers the --resume path as well as in-session restarts.
        if (checkpoint.ResumeExecutorId is not null)
            orchestrator.SetResumeExecutorId(checkpoint.ResumeExecutorId);

        // Restore the state machine's current state so --resume picks up at the correct
        // workflow state (e.g. "Testing") instead of restarting from the initial state.
        // checkpoint.CurrentStateName is saved at every compaction and at every abort.
        if (checkpoint.CurrentStateName is not null)
            orchestrator.SetResumeStateName(checkpoint.CurrentStateName);
        if (orchestrator is AgentOrchestrator agentOrch && checkpoint.StateMachineState is { } smState)
            agentOrch.SetResumeSnapshot(smState);

        // Restore Magentic loop-counter state so the orchestrator resumes at the correct
        // round without replaying the planning phase.
        if (orchestrator is MagenticOrchestrator magentic && checkpoint.MagenticState is { } magState)
            magentic.SetResumeState(magState);

        // Cancellation
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            AnsiConsole.MarkupLine("\n[dim]Cancellation requested...[/]");
        };

        // DevUI — start before the main loop so the browser can connect early.
        DevUIServer? devUI = null;
        if (settings.DevUI)
        {
            devUI = new DevUIServer();
            await devUI.StartAsync();
            AnsiConsole.MarkupLine($"[dim]DevUI → [link]http://localhost:{devUI.Port}[/][/]");
        }
        await using var _devUI = devUI;

        devUI?.BroadcastSessionStart(checkpoint.SessionId, task, config.Name);

        // Run the session
        var runner = new SessionRunner(
            orchestrator, compactor, activeStore, approvalService,
            eventEmitter, telemetry, modelIdByAgent, devUI, configPath,
            maxIterations: config.Termination?.ResolveMaxIterations() ?? 0,
            contextBudget: config.ContextBudget,
            contextWindowRecorder: ctxRecorder,
            sessionMetrics: sessionMetrics,
            postmortemWriter: snapshotWriter,
            quiet: jsonMode);

        if (!isNewSession && eventEmitter is not null)
            _ = eventEmitter.EmitAsync(EventTypes.ResumeStarted,
                payload: new { session = checkpoint.SessionId, turns_prior = checkpoint.Messages.Count });

        var result = await runner.RunAsync(task, checkpoint, settings.HumanInTheLoop, settings.ShowTools, cts.Token);

        if (!isNewSession && result.Succeeded && eventEmitter is not null)
            _ = eventEmitter.EmitAsync(EventTypes.ResumeCompleted,
                payload: new { session = checkpoint.SessionId });
        devUI?.BroadcastSessionEnd(result.Succeeded, result.ErrorMessage);

        // Mark complete on success (distinct from per-turn saves above).
        if (result.Succeeded)
        {
            checkpoint.IsComplete = true;
            await activeStore.SaveAsync(checkpoint, CancellationToken.None);
        }

        // Post-session skill curation (best-effort — never fails the run)
        if (skillCurator is not null && result.Succeeded)
        {
            try
            {
                await (eventEmitter?.EmitAsync(EventTypes.SkillCurationStart,
                    payload: new { session = checkpoint.SessionId, source = "run" }) ?? Task.CompletedTask);

                var curationResult = await skillCurator.RunAsync(
                    checkpoint, result.Messages, CancellationToken.None, source: "run");

                await (eventEmitter?.EmitAsync(EventTypes.SkillCurationComplete,
                    payload: new
                    {
                        session        = checkpoint.SessionId,
                        source         = "run",
                        outcome        = curationResult.Outcome.ToString().ToLowerInvariant(),
                        slug           = curationResult.Slug,
                        path           = curationResult.Path,
                        turns_digested = curationResult.TurnsDigested,
                        failure_reason = curationResult.FailureReason,
                    }) ?? Task.CompletedTask);

                if (curationResult.WroteSkill)
                    AnsiConsole.MarkupLine(
                        $"[green]✓ Skill {(curationResult.Outcome == SkillCurationOutcome.Updated ? "updated" : "curated")}:[/] " +
                        $"[bold]{Markup.Escape(curationResult.Slug!)}[/]  [dim]{Markup.Escape(curationResult.Path!)}[/]");
                else if (curationResult.Outcome == SkillCurationOutcome.Failed)
                    AnsiConsole.MarkupLine(
                        $"[dim yellow]Skill curation failed:[/] {Markup.Escape(curationResult.FailureReason ?? "unknown error")}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[dim yellow]Skill curation failed:[/] {Markup.Escape(ex.Message)}");
            }
        }

        // Post-session repository memory extraction (best-effort — never fails the run).
        if (repoMemoryExtractor is not null && result.Succeeded)
        {
            try
            {
                var candidates = await repoMemoryExtractor.ExtractAsync(
                    sessionId: checkpoint.SessionId, CancellationToken.None);
                if (candidates.Count > 0)
                    AnsiConsole.MarkupLine(
                        $"[dim]Repository memory: {candidates.Count} new candidate(s) extracted. " +
                        $"Run [bold]fuseraft memory review[/] to approve.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[dim yellow]Repository memory extraction failed:[/] {Markup.Escape(ex.Message)}");
            }
        }

        // Context window visualization — render after the run so all snapshot data is flushed.
        var ctxVizPath    = fuseraft.Core.FuseraftPaths.ExpandSessionId(fuseraft.Core.FuseraftPaths.LocalCtxViz, checkpoint.SessionId);
        var ctxEventsPath = Path.Combine(Path.GetDirectoryName(ctxSnapshotsPath)!, "events.jsonl");
        if (await fuseraft.Cli.Display.ContextWindowRenderer.RenderAsync(ctxSnapshotsPath, ctxVizPath, checkpoint.SessionId, ctxEventsPath))
            AnsiConsole.MarkupLine($"[dim]Context viz → {Markup.Escape(ctxVizPath)}[/]");

        // Summary
        MessageRenderer.RenderSummary(result.Messages, result.Succeeded, result.Elapsed, result.ErrorMessage);

        // SLO compliance summary (only shown when validators were actually exercised).
        var complianceTracker = governanceKernel.SloEngine.Get("policy-compliance");
        if (complianceTracker?.EventCount > 0)
        {
            var sli        = complianceTracker.CurrentSli();
            var budget     = complianceTracker.RemainingBudget();
            var sloColor   = sli >= 95.0 ? "green" : sli >= 80.0 ? "yellow" : "red";
            var budgetText = budget >= 0
                ? $"[dim]{budget:F0} error budget remaining[/]"
                : $"[red]budget exhausted ({Math.Abs(budget):F0} over)[/]";
            AnsiConsole.MarkupLine(
                $"[{sloColor}]Policy compliance:[/] [bold]{sli:F1}%[/]  {budgetText}");

            var alerts = complianceTracker.CheckBurnRateAlerts();
            foreach (var alert in alerts)
            {
                var alertColor = alert.Severity switch
                {
                    BurnRateSeverity.Critical => "red",
                    BurnRateSeverity.Page     => "bold red",
                    _                         => "yellow",
                };
                AnsiConsole.MarkupLine(
                    $"  [{alertColor}]⚠ Burn rate alert:[/] [dim]{alert.Name} ({alert.Rate}× sustainable)[/]");
            }
            AnsiConsole.WriteLine();
        }

        // Optional transcript
        if (settings.OutputPath is { } outPath)
            await SaveTranscriptAsync(task, result.Messages, outPath);

        // CI mode: exit 2 if any acceptance criterion is FAIL in test-report.json.
        CiCheckResult? ciCheck = null;
        var exitCode = result.Succeeded ? 0 : 1;
        if (settings.Ci && result.Succeeded && config.Validation?.TestReportPath is { } reportPath)
        {
            ciCheck  = await CheckCiAsync(reportPath);
            exitCode = ciCheck.ExitCode;
        }

        if (jsonMode)
            EmitJsonSummary(checkpoint.SessionId, task, configPath, result, ciCheck, settings.OutputPath, exitCode);

        return exitCode;
    }

    /// <summary>
    /// A standalone Spectre console bound to stderr (independent of the ambient
    /// <see cref="AnsiConsole.Console"/>). Every diagnostic that can fire before the config —
    /// and therefore <c>Output.Json</c> — has loaded is written through this instance instead of
    /// the ambient one, so it is guaranteed to land on stderr regardless of whether JSON mode
    /// ends up enabled. Markup/coloring still renders normally when stderr is a terminal.
    /// </summary>
    private static readonly IAnsiConsole StderrConsole = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(Console.Error),
    });

    /// <summary>
    /// Points <see cref="AnsiConsole.Console"/> (the ambient console used by the rest of the
    /// command, once JSON mode is confirmed) at stderr for the remainder of the process. Used by
    /// <c>--json</c> / <c>Output.Json</c> so stdout stays a clean channel for the single JSON
    /// summary object printed at the end of the run — <see cref="Console.Out"/> itself is
    /// untouched, so <see cref="EmitJsonSummary"/> below still lands on the real stdout.
    /// </summary>
    private static void RedirectAnsiConsoleToStderr() => AnsiConsole.Console = StderrConsole;

    private static readonly JsonSerializerOptions JsonSummaryOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Prints a minimal JSON error summary to stdout for a run that never reached a completed
    /// session — a setup check (work dir, resume, spec, config load, API key validation,
    /// task-file resolution, prompt-injection rejection, pre-loop compaction) failed. Callers
    /// before the config loads pass <c>settings.Json</c> (the CLI flag), since that's the one case
    /// where JSON mode is known for certain that early; callers after the config loads pass the
    /// fully-resolved <c>jsonMode</c> instead, so a config-only <c>Output.Json: true</c> run gets
    /// the same guarantee for every failure past that point — see the comment at the top of
    /// <see cref="ExecuteAsync"/>. Mirrors <see cref="EmitJsonSummary"/>'s field set so callers
    /// can parse both with the same schema; fields that don't apply yet (no session ever started)
    /// are zeroed/nulled rather than omitted.
    /// </summary>
    private static void EmitJsonErrorSummary(string? configPath, string errorMessage, int exitCode)
    {
        var summary = new
        {
            session_id      = (string?)null,
            task            = (string?)null,
            config          = configPath,
            succeeded       = false,
            error_message   = errorMessage,
            exit_code       = exitCode,
            turns           = 0,
            elapsed_seconds = 0.0,
            tokens          = new { input = 0, output = 0 },
            transcript_path = (string?)null,
            ci              = (object?)null,
        };

        Console.Out.WriteLine(JsonSerializer.Serialize(summary, JsonSummaryOptions));
    }

    /// <summary>
    /// Calls <see cref="EmitJsonErrorSummary"/> only when <paramref name="jsonFlag"/> is set.
    /// Named separately from the unconditional overload so early-return call sites read as a
    /// single, self-explanatory statement.
    /// </summary>
    private static void EmitJsonErrorIfNeeded(bool jsonFlag, string? configPath, string errorMessage, int exitCode)
    {
        if (jsonFlag)
            EmitJsonErrorSummary(configPath, errorMessage, exitCode);
    }

    /// <summary>
    /// Prints a single-line JSON object summarising the completed session to stdout, for
    /// scripts invoked via <c>--json</c> / <c>Output.Json</c> that need a structured result
    /// instead of parsing the transcript or console output.
    /// </summary>
    private static void EmitJsonSummary(
        string sessionId,
        string task,
        string configPath,
        SessionResult result,
        CiCheckResult? ciCheck,
        string? transcriptPath,
        int exitCode)
    {
        var summary = new
        {
            session_id      = sessionId,
            task,
            config          = configPath,
            succeeded       = result.Succeeded,
            error_message   = result.ErrorMessage,
            exit_code       = exitCode,
            turns           = result.Messages.Count(m => m.Role == MessageRole.Assistant),
            elapsed_seconds = Math.Round(result.Elapsed.TotalSeconds, 2),
            tokens          = new
            {
                input  = result.Messages.Sum(m => m.Usage?.InputTokens ?? 0),
                output = result.Messages.Sum(m => m.Usage?.OutputTokens ?? 0),
            },
            transcript_path = transcriptPath,
            ci = ciCheck is null ? null : new
            {
                passed          = ciCheck.Passed,
                skipped         = ciCheck.Skipped,
                failed_criteria = ciCheck.FailedCriteria,
            },
        };

        Console.Out.WriteLine(JsonSerializer.Serialize(summary, JsonSummaryOptions));
    }

    // Helpers

    /// <summary>
    /// Searches the skill index for skills relevant to the current task and prepends
    /// a context message to the checkpoint so agents know which skills to invoke.
    /// No-op when the index is empty or the query matches nothing.
    /// </summary>
    private static async Task InjectSkillContextAsync(
        string task,
        SkillCurationConfig curationConfig,
        SessionCheckpoint checkpoint,
        CancellationToken ct)
    {
        try
        {
            var indexPath = string.IsNullOrWhiteSpace(curationConfig.IndexPath)
                ? fuseraft.Core.FuseraftPaths.GlobalSkillsIndex
                : curationConfig.IndexPath;

            if (!File.Exists(indexPath)) return;

            await using var index = new SkillIndex(indexPath);
            var matches = await index.SearchAsync(task, curationConfig.IndexTopN, ct);
            if (matches.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("SKILLS RELEVANT TO THIS TASK:");
            sb.AppendLine();
            for (var i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                sb.Append($"{i + 1}. **{m.Slug}**");
                if (!string.IsNullOrWhiteSpace(m.Description))
                    sb.Append($" — {m.Description}");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(m.Excerpt))
                    sb.AppendLine($"   _{m.Excerpt}_");
            }
            sb.AppendLine();
            sb.AppendLine("Invoke a skill with the load_skill tool using its slug name.");

            checkpoint.Messages.Add(new AgentMessage
            {
                AgentName = AgentNames.System,
                Content   = sb.ToString().TrimEnd(),
                Role      = "user",
                TurnIndex = 0,
            });
        }
        catch (Exception)
        {
            // Index lookup is best-effort — never fail the run
        }
    }

    /// <summary>
    /// Builds the active session store for a run command.
    /// Performs a lightweight config load to read the Checkpoint section; falls back to
    /// the global injected store on any error or when no Checkpoint config is present.
    /// </summary>
    private static ISessionStore BuildActiveStore(
        string configPath,
        ILoggerFactory loggerFactory,
        ISessionStore globalStore)
    {
        CheckpointConfig? checkpointConfig = null;
        if (File.Exists(configPath))
        {
            try { checkpointConfig = OrchestratorConfigLoader.LoadConfig(configPath).Checkpoint; }
            catch (Exception ex) { loggerFactory.CreateLogger<RunCommand>().LogWarning(ex, "[BuildActiveStore] {Message}", ex.Message); }
        }

        if (checkpointConfig?.Mode?.Equals("memory", StringComparison.OrdinalIgnoreCase) == true)
            return new InMemorySessionStore();

        if (!string.IsNullOrWhiteSpace(checkpointConfig?.Path))
        {
            var resolvedPath = Path.GetFullPath(checkpointConfig.Path);
            return new JsonSessionStore(loggerFactory.CreateLogger<JsonSessionStore>(), resolvedPath);
        }

        return globalStore;
    }

    private static async Task<SessionCheckpoint?> ResolveCheckpointAsync(
        string sessionIdHint,
        ISessionStore store)
    {
        if (string.IsNullOrWhiteSpace(sessionIdHint))
        {
            var index    = await store.ListIndexAsync();
            var incomplete = index.Where(e => !e.IsComplete).ToList();

            if (incomplete.Count == 0)
            {
                StderrConsole.MarkupLine("[yellow]No incomplete sessions found.[/]");
                return null;
            }

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<SessionIndexEntry>()
                    .Title("Select a session to resume:")
                    .UseConverter(e =>
                    {
                        var proj = e.WorkingDirectory is { } wd
                            ? string.Join("/", wd.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)[^Math.Min(2, wd.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length)..])
                            : "?";
                        return $"[bold]{e.SessionId}[/]  {e.TurnCount} turns  " +
                               $"[dim]{e.LastUpdatedAt:yyyy-MM-dd HH:mm}  {proj}  {StringHelpers.Truncate(e.Task, 50)}[/]";
                    })
                    .AddChoices(incomplete));

            return await store.LoadAsync(selected.SessionId);
        }

        var checkpoint = await store.LoadAsync(sessionIdHint);

        if (checkpoint is null)
        {
            StderrConsole.MarkupLine($"[red]✗ Session not found:[/] {Markup.Escape(sessionIdHint)}");
            return null;
        }

        if (checkpoint.IsComplete)
        {
            StderrConsole.MarkupLine($"[yellow]Session {sessionIdHint} is already complete.[/]");
            return null;
        }

        return checkpoint;
    }

    private static async Task SaveTranscriptAsync(
        string task,
        IReadOnlyList<AgentMessage> messages,
        string outputPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var writer = new StreamWriter(outputPath, append: true);
            await writer.WriteLineAsync("# Session Transcript");
            await writer.WriteLineAsync($"**Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            await writer.WriteLineAsync($"**Task:** {task}");
            await writer.WriteLineAsync();

            foreach (var msg in messages)
            {
                await writer.WriteLineAsync("---");

                if (msg.Role == MessageRole.User)
                {
                    await writer.WriteLineAsync($"## [Human] — Redirect");
                }
                else
                {
                    var tokenNote = msg.Usage is { } u
                        ? $"  ·  in:{u.InputTokens:N0} out:{u.OutputTokens:N0}"
                        : string.Empty;
                    await writer.WriteLineAsync($"## [{msg.AgentName}] — Turn {msg.TurnIndex + 1}{tokenNote}");
                }

                await writer.WriteLineAsync();
                await writer.WriteLineAsync(msg.Content);
                await writer.WriteLineAsync();
            }

            AnsiConsole.MarkupLine($"[dim]Transcript saved → {Markup.Escape(outputPath)}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Could not save transcript: {Markup.Escape(ex.Message)}[/]");
        }
    }

    /// <summary>
    /// Result of the post-run CI check against <c>test-report.json</c>.
    /// </summary>
    private sealed record CiCheckResult(int ExitCode, bool Passed, bool Skipped, List<string> FailedCriteria);

    /// <summary>
    /// Reads test-report.json and returns exit code 2 if any criterion has status FAIL, 0 otherwise.
    /// Logs a summary to the console so CI output is self-explanatory.
    /// </summary>
    private static async Task<CiCheckResult> CheckCiAsync(string reportPath)
    {
        if (!File.Exists(reportPath))
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ CI check skipped — test-report.json not found at '{Markup.Escape(reportPath)}'.[/]");
            return new CiCheckResult(0, Passed: true, Skipped: true, FailedCriteria: []);
        }

        try
        {
            var json    = await File.ReadAllTextAsync(reportPath);
            var report  = JsonSerializer.Deserialize<CiTestReport>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var fails = report?.Results?
                .Where(r => string.Equals(r.Status, "FAIL", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Criterion ?? "(unknown)")
                .ToList() ?? [];

            if (fails.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]✓ CI check passed — all acceptance criteria PASS.[/]");
                return new CiCheckResult(0, Passed: true, Skipped: false, FailedCriteria: []);
            }

            AnsiConsole.MarkupLine($"[red]✗ CI check failed — {fails.Count} criterion/criteria FAIL:[/]");
            foreach (var f in fails)
                AnsiConsole.MarkupLine($"  [red]FAIL[/] {Markup.Escape(f)}");

            return new CiCheckResult(2, Passed: false, Skipped: false, FailedCriteria: fails);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ CI check skipped — could not parse test-report.json: {Markup.Escape(ex.Message)}[/]");
            return new CiCheckResult(0, Passed: true, Skipped: true, FailedCriteria: []);
        }
    }

    // Minimal DTOs for the CI report check — no dependency on HandoffToReviewerValidator internals.
    private sealed record CiTestReport
    {
        public List<CiTestResult>? Results { get; init; }
    }
    private sealed record CiTestResult
    {
        public string? Criterion { get; init; }
        public string? Status    { get; init; }
    }

    /// <summary>
    /// Returns the resolved absolute working directory for the session, or null to keep the CWD.
    /// Priority: --work-dir flag > Security.FileSystemSandboxPath in config > CWD (null = no change).
    /// </summary>
    private static IReadOnlyList<string> DiscoverSkills()
    {
        // Same order as BuildSkillsProvider: project-native → project cross-client →
        // user-native → user cross-client → built-in.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".fuseraft", "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), ".agents",   "skills"),
            FuseraftPaths.GlobalSkills,
            Path.Combine(home, ".agents",   "skills"),
            Path.Combine(AppContext.BaseDirectory, "skills"),
        };
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var skillDir in Directory.EnumerateDirectories(dir))
            {
                if (!File.Exists(Path.Combine(skillDir, "SKILL.md"))) continue;
                var name = Path.GetFileName(skillDir);
                if (seen.Add(name))
                    names.Add(name);
            }
        }
        return names;
    }

    private static string? ResolveWorkDir(string? flagValue, string absoluteConfigPath, ILogger? logger = null)
    {
        if (!string.IsNullOrWhiteSpace(flagValue))
            return FuseraftPaths.ExpandPath(flagValue);

        // Fall back to the sandbox path declared in the config (lightweight load).
        if (File.Exists(absoluteConfigPath))
        {
            try
            {
                var sandboxPath = OrchestratorConfigLoader.LoadConfig(absoluteConfigPath).Security?.FileSystemSandboxPath;
                if (!string.IsNullOrWhiteSpace(sandboxPath))
                    return FuseraftPaths.ExpandPath(sandboxPath);
            }
            catch (Exception ex) { logger?.LogWarning(ex, "[ResolveWorkDir] {Message}", ex.Message); }
        }

        return null; // keep CWD
    }

    private const string DefaultDemoTask = """
        Build a CLI TODO app in Rust with:
        - Persistent storage using SQLite (via rusqlite)
        - Support for tags on each task
        - Filtering by tag and by completion status
        - Commands: add, list, done, delete, tag
        - Comprehensive unit tests for all commands
        """;
}
