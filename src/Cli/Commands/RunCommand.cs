using System.ComponentModel;
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
    [Description("Skip the Figlet banner (useful in CI / piped output).")]
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
        if (!settings.NoBanner)
            MessageRenderer.RenderBanner();

        // Determine the config path early so we can build the right session store before
        // loading the full config. When resuming, checkpoint.ConfigPath will refine this later.
        // Resolve to absolute immediately so it stays valid after a potential CWD change below.
        var configPath = Path.GetFullPath(settings.ConfigPath ?? "config/orchestration.yaml");

        // Resolve the effective working directory: --work-dir > config sandbox path > CWD.
        // This must happen before BuildActiveStore so that all subsequent relative-path
        // resolutions (checkpoint path, validation paths, change log, etc.) are rooted here.
        var workDir = ResolveWorkDir(settings.WorkDir, configPath);
        if (workDir is not null)
        {
            if (!Directory.Exists(workDir))
            {
                AnsiConsole.MarkupLine($"[red]✗ Work directory not found:[/] {Markup.Escape(workDir)}");
                return 1;
            }
            Directory.SetCurrentDirectory(workDir);
            AnsiConsole.MarkupLine($"[dim]Working directory → {Markup.Escape(workDir)}[/]");
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
                AnsiConsole.MarkupLine("[yellow]⚠ CheckpointMode is 'memory' — sessions are not persisted and cannot be resumed.[/]");
                return 1;
            }

            // Try the active (project-local) store first; fall back to the global store so
            // sessions created before a CheckpointPath was configured can still be resumed.
            checkpoint = await ResolveCheckpointAsync(settings.Resume, activeStore);
            if (checkpoint is null && !ReferenceEquals(activeStore, sessionStore))
                checkpoint = await ResolveCheckpointAsync(settings.Resume, sessionStore);
            if (checkpoint is null) return 1;

            // TurnIndex of the last message equals the highest turn number, accounting for
            // any previous compactions where Messages.Count < total turns elapsed.
            var turnsComplete = checkpoint.Messages.Count > 0
                ? checkpoint.Messages[^1].TurnIndex + 1
                : 0;

            AnsiConsole.MarkupLine($"[dim]Resuming session [bold]{checkpoint.SessionId}[/] " +
                                   $"({turnsComplete} turns already complete)[/]");
        }

        // Reconcile config path: an existing checkpoint always knows its own config.
        configPath = checkpoint?.ConfigPath ?? configPath;

        OrchestrationConfig config;
        IOrchestrator orchestrator;
        McpSessionManager mcpManager;
        ConversationCompactor? compactor;
        ChangeTracker? changeTracker;
        EventEmitter? eventEmitter;
        AgentGovernance.GovernanceKernel governanceKernel;

        var approvalService = new ConsoleHumanApprovalService();

        try
        {
            (orchestrator, config, mcpManager, compactor, changeTracker, eventEmitter, governanceKernel) =
                await OrchestratorBuilder.BuildAsync(configPath, loggerFactory, pluginRegistry, approvalService, settings.HumanInTheLoop);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Config error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        await using var _mcp = mcpManager;
        using var _governance = governanceKernel;

        // Build a fast agent→modelId lookup for telemetry tagging.
        var modelIdByAgent = config.Agents
            .ToDictionary(
                a => a.Name,
                a => string.IsNullOrWhiteSpace(a.Model.ModelId) ? "unknown" : a.Model.ModelId,
                StringComparer.OrdinalIgnoreCase);

        using var telemetry = FuseraftTelemetry.Create(config.Telemetry, config.Name);

        MessageRenderer.RenderConfigSummary(config);

        // Validate API keys early so a bad/missing key surfaces before the session starts.
        try
        {
            await OrchestratorBuilder.ValidateApiKeysAsync(config);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ API key validation failed:[/] {Markup.Escape(ex.Message)}");
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
                return 1;
            }

            task = (await File.ReadAllTextAsync(settings.TaskFile)).Trim();

            if (string.IsNullOrWhiteSpace(task))
            {
                AnsiConsole.MarkupLine($"[red]✗ Task file is empty:[/] {Markup.Escape(settings.TaskFile)}");
                return 1;
            }
        }
        else
        {
            task = settings.Task?.Trim();
        }

        if (string.IsNullOrEmpty(task))
        {
            task = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]Task[/] [dim](Enter to use demo)[/]:")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(task))
                task = DefaultDemoTask;
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
                return 1;
            }
        }

        if (settings.HumanInTheLoop)
            AnsiConsole.MarkupLine("[dim]HITL mode enabled — you will be prompted after each agent turn.[/]\n");

        // Prepare checkpoint
        checkpoint ??= new SessionCheckpoint
        {
            SessionId  = Guid.NewGuid().ToString("N")[..8],
            Task       = task,
            ConfigPath = configPath
        };

        // Stamp the session ID on the change tracker so check 8 in TestReportValid filters
        // to only commands recorded in this session, preventing prior-session contamination.
        if (changeTracker is not null)
            await changeTracker.SetSessionIdAsync(checkpoint.SessionId);

        // Stamp the session ID on the event emitter and orchestrator so every event carries it.
        eventEmitter?.SetSessionId(checkpoint.SessionId);
        orchestrator.SetSessionId(checkpoint.SessionId);

        // Seed structured task model (resumed sessions may already have it in the checkpoint).
        orchestrator.SetStructuredTask(
            checkpoint.StructuredTask ?? fuseraft.Core.Models.TaskModel.FromGoal(task));

        // Compact before the stream starts if the existing history is already over the threshold.
        // This covers the resume case where a prior session accumulated too many turns.
        if (compactor?.ShouldCompact(checkpoint.Messages) == true)
        {
            checkpoint = await ApplyCompactionAsync(task, checkpoint, compactor, activeStore, orchestrator);
            AnsiConsole.MarkupLine("[dim]History compacted before resuming.[/]");
        }

        // If the checkpoint carries an explicit resume executor hint (written before a prior
        // compaction or crash), push it to the orchestrator so the first StreamAsync starts
        // from the correct agent.  This covers the --resume path as well as in-session restarts.
        if (checkpoint.ResumeExecutorId is not null)
            orchestrator.SetResumeExecutorId(checkpoint.ResumeExecutorId);

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
            maxIterations: config.Termination?.ResolveMaxIterations() ?? 0);

        var result = await runner.RunAsync(task, checkpoint, settings.HumanInTheLoop, settings.ShowTools, cts.Token);

        devUI?.BroadcastSessionEnd(result.Succeeded, result.ErrorMessage);

        // Mark complete on success (distinct from per-turn saves above).
        if (result.Succeeded)
        {
            checkpoint.IsComplete = true;
            await activeStore.SaveAsync(checkpoint, CancellationToken.None);
        }

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
        if (settings.Ci && result.Succeeded && config.Validation?.TestReportPath is { } reportPath)
        {
            var ciResult = await CheckCiAsync(reportPath);
            if (ciResult != 0) return ciResult;
        }

        return result.Succeeded ? 0 : 1;
    }

    // Helpers

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
            try { checkpointConfig = OrchestratorBuilder.LoadConfig(configPath).Checkpoint; }
            catch { /* errors will surface later during full BuildAsync */ }
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
            var all = await store.ListAsync();
            var incomplete = all.Where(s => !s.IsComplete).ToList();

            if (incomplete.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No incomplete sessions found.[/]");
                return null;
            }

            return AnsiConsole.Prompt(
                new SelectionPrompt<SessionCheckpoint>()
                    .Title("Select a session to resume:")
                    .UseConverter(s =>
                        $"[bold]{s.SessionId}[/]  {s.Messages.Count} turns  " +
                        $"[dim]{s.LastUpdatedAt:yyyy-MM-dd HH:mm}  {StringHelpers.Truncate(s.Task, 60)}[/]")
                    .AddChoices(incomplete));
        }

        var checkpoint = await store.LoadAsync(sessionIdHint);

        if (checkpoint is null)
        {
            AnsiConsole.MarkupLine($"[red]✗ Session not found:[/] {Markup.Escape(sessionIdHint)}");
            return null;
        }

        if (checkpoint.IsComplete)
        {
            AnsiConsole.MarkupLine($"[yellow]Session {sessionIdHint} is already complete.[/]");
            return null;
        }

        return checkpoint;
    }

    private static async Task<SessionCheckpoint> ApplyCompactionAsync(
        string task,
        SessionCheckpoint checkpoint,
        ConversationCompactor compactor,
        ISessionStore store,
        IOrchestrator? orchestrator = null,
        CancellationToken cancellationToken = default)
    {
        // Only set ResumeExecutorId for non-Magentic orchestrators. MagenticOrchestrator
        // ignores it (SetResumeExecutorId is a no-op), and the last assistant message in a
        // Magentic session is typically a manager tag like "[MagenticManager:Final]", which
        // would write a misleading value into the persisted checkpoint.
        if (orchestrator is not MagenticOrchestrator)
        {
            checkpoint.ResumeExecutorId = checkpoint.Messages
                .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.AgentName))
                ?.AgentName
                ?.ToLowerInvariant();
        }

        if (compactor.IsWindowMode)
        {
            var trimmed = compactor.TrimToWindow(checkpoint.Messages);
            checkpoint.Messages.Clear();
            checkpoint.Messages.AddRange(trimmed);
            checkpoint.LastUpdatedAt = DateTime.UtcNow;
            await store.SaveAsync(checkpoint, cancellationToken);
            return checkpoint;
        }

        var (summary, retained) = await compactor.CompactAsync(task, checkpoint.Messages, cancellationToken);

        checkpoint.Messages.Clear();
        checkpoint.Messages.Add(summary);
        checkpoint.Messages.AddRange(retained);
        checkpoint.LastUpdatedAt = DateTime.UtcNow;

        await store.SaveAsync(checkpoint, cancellationToken);
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

                if (msg.Role == "user")
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
    /// Reads test-report.json and returns 2 if any criterion has status FAIL, 0 otherwise.
    /// Logs a summary to the console so CI output is self-explanatory.
    /// </summary>
    private static async Task<int> CheckCiAsync(string reportPath)
    {
        if (!File.Exists(reportPath))
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ CI check skipped — test-report.json not found at '{Markup.Escape(reportPath)}'.[/]");
            return 0;
        }

        try
        {
            var json    = await File.ReadAllTextAsync(reportPath);
            var report  = System.Text.Json.JsonSerializer.Deserialize<CiTestReport>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var fails = report?.Results?
                .Where(r => string.Equals(r.Status, "FAIL", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? [];

            if (fails.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]✓ CI check passed — all acceptance criteria PASS.[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"[red]✗ CI check failed — {fails.Count} criterion/criteria FAIL:[/]");
            foreach (var f in fails)
                AnsiConsole.MarkupLine($"  [red]FAIL[/] {Markup.Escape(f.Criterion ?? "(unknown)")}");

            return 2;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ CI check skipped — could not parse test-report.json: {Markup.Escape(ex.Message)}[/]");
            return 0;
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
    private static string? ResolveWorkDir(string? flagValue, string absoluteConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(flagValue))
            return Path.GetFullPath(ProcessHelper.ExpandHome(flagValue));

        // Fall back to the sandbox path declared in the config (lightweight load).
        if (File.Exists(absoluteConfigPath))
        {
            try
            {
                var sandboxPath = OrchestratorBuilder.LoadConfig(absoluteConfigPath).Security?.FileSystemSandboxPath;
                if (!string.IsNullOrWhiteSpace(sandboxPath))
                    return Path.GetFullPath(ProcessHelper.ExpandHome(sandboxPath));
            }
            catch { /* errors surface later in full BuildAsync */ }
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
