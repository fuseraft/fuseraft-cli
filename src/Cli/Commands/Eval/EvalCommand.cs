using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Orchestration;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli.Commands.Eval;

public sealed class EvalSettings : CommandSettings
{
    [CommandArgument(0, "[suite]")]
    [Description("Path to the eval suite YAML or JSON file (default: .fuseraft/evals/suite.yaml).")]
    public string? Suite { get; set; }

    [CommandOption("-c|--config")]
    [Description("Override the suite-level team config path.")]
    public string? ConfigPath { get; set; }

    [CommandOption("-o|--output")]
    [Description("Write per-case results as JSONL to this file.")]
    public string? OutputPath { get; set; }

    [CommandOption("--filter")]
    [Description("Run only cases whose id or tag contains this value (case-insensitive substring).")]
    public string? Filter { get; set; }

    [CommandOption("--timeout")]
    [Description("Per-case timeout in seconds. 0 = no timeout (default).")]
    public int TimeoutSeconds { get; set; }

    [CommandOption("--no-banner")]
    [Description("Skip the suite header.")]
    public bool NoBanner { get; set; }

    [CommandOption("--ci")]
    [Description("Exit 1 if any case fails (for CI pipelines).")]
    public bool Ci { get; set; }
}

/// <summary>
/// Runs an eval suite against a team config and reports pass/fail per case.
/// Usage: fuseraft eval run [suite.yaml] [--config team.yaml] [--filter tag] [--output results.jsonl]
/// </summary>
public sealed class EvalCommand(ILoggerFactory loggerFactory, PluginRegistry pluginRegistry)
    : AsyncCommand<EvalSettings>
{
    internal static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    internal static readonly JsonSerializerOptions JsonReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    protected override async Task<int> ExecuteAsync(CommandContext context, EvalSettings settings, CancellationToken cancellationToken)
    {
        var suitePath = Path.GetFullPath(settings.Suite ?? ".fuseraft/evals/suite.yaml");

        if (!File.Exists(suitePath))
        {
            AnsiConsole.MarkupLine($"[red]✗ Suite file not found:[/] {Markup.Escape(suitePath)}");
            return 1;
        }

        EvalSuite suite;
        try
        {
            suite = LoadSuite(suitePath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Failed to load suite:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (suite.Cases.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ Suite has no cases.[/]");
            return 0;
        }

        var cases = ApplyFilter(suite.Cases, settings.Filter);
        if (cases.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ No cases match filter '{Markup.Escape(settings.Filter ?? "")}'.[/]");
            return 0;
        }

        if (!settings.NoBanner)
        {
            AnsiConsole.MarkupLine($"[bold]Eval suite:[/] {Markup.Escape(suite.Name)}  [dim]{cases.Count} case(s)[/]");
            if (settings.TimeoutSeconds > 0)
                AnsiConsole.MarkupLine($"[dim]Per-case timeout: {settings.TimeoutSeconds}s[/]");
            AnsiConsole.WriteLine();
        }

        var results         = new List<EvalCaseResult>();
        // Eval runs are unattended by definition (no --hitl, often no TTY at all — CI).
        // ConsoleHumanApprovalService would block on Console.ReadLine() the moment a
        // validator gets stuck or an agent reports BLOCKED, since SessionRunner escalates
        // to those prompts regardless of hitlMode. Use the non-interactive service so that
        // escalation resolves to a deterministic failure instead of a misleading prompt.
        var approvalService = new NonInteractiveHumanApprovalService();

        // Live progress reporting, active whenever -o/--output is set (no separate flag —
        // the same file that already opts in to persisted results is the natural signal
        // that something wants to observe this run). Two files, updated incrementally
        // instead of once at the very end:
        //   <output>          — one JSON line per COMPLETED case, appended+flushed as each
        //                       case finishes, so `tail -f` (or a crash/hang mid-run) never
        //                       loses already-completed results the way a single end-of-run
        //                       batch write would.
        //   <output>.status.json — small, cheaply-pollable snapshot: which case is running
        //                       right now, and the pass/fail tally so far. Overwritten (not
        //                       appended) after every state change.
        var statusPath  = settings.OutputPath is not null ? settings.OutputPath + ".status.json" : null;
        var startedAt   = DateTime.UtcNow;
        StreamWriter? jsonlWriter = null;
        if (settings.OutputPath is not null)
        {
            var dir = Path.GetDirectoryName(settings.OutputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            jsonlWriter = new StreamWriter(settings.OutputPath, append: false) { AutoFlush = true };
        }

        async Task WriteStatusAsync(string? currentCaseId, string state)
        {
            if (statusPath is null) return;
            var status = new EvalRunStatus(
                Suite:       suite.Name,
                Total:       cases.Count,
                Completed:   results.Count,
                Passed:      results.Count(r => r.Passed),
                Failed:      results.Count(r => !r.Passed),
                CurrentCase: currentCaseId,
                State:       state,
                StartedAt:   startedAt,
                UpdatedAt:   DateTime.UtcNow);
            try
            {
                await File.WriteAllTextAsync(statusPath, JsonSerializer.Serialize(status, JsonWriteOpts));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ Could not write status: {Markup.Escape(ex.Message)}[/]");
            }
        }

        async Task RecordResultAsync(EvalCaseResult result)
        {
            results.Add(result);
            if (jsonlWriter is not null)
                await jsonlWriter.WriteLineAsync(JsonSerializer.Serialize(result, JsonWriteOpts));
        }

        foreach (var evalCase in cases)
        {
            await WriteStatusAsync(evalCase.Id, "running");

            var configPath = Path.GetFullPath(
                evalCase.Config
                ?? settings.ConfigPath
                ?? suite.Config
                ?? ".fuseraft/config/orchestration.yaml");

            if (!File.Exists(configPath))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(evalCase.Id.PadRight(40))}  config not found: {Markup.Escape(configPath)}");
                await RecordResultAsync(Failed(evalCase.Id, "—", $"config not found: {configPath}"));
                continue;
            }

            string? task = null;
            if (evalCase.TaskFile is not null)
            {
                var absFile = Path.IsPathRooted(evalCase.TaskFile)
                    ? evalCase.TaskFile
                    : Path.GetFullPath(evalCase.TaskFile);
                if (!File.Exists(absFile))
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(evalCase.Id.PadRight(40))}  task_file not found: {Markup.Escape(absFile)}");
                    await RecordResultAsync(Failed(evalCase.Id, "—", $"task_file not found: {absFile}"));
                    continue;
                }
                task = (await File.ReadAllTextAsync(absFile, cancellationToken)).Trim();
            }
            else
            {
                task = evalCase.Task?.Trim();
            }

            if (string.IsNullOrWhiteSpace(task))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(evalCase.Id.PadRight(40))}  no task defined");
                await RecordResultAsync(Failed(evalCase.Id, "—", "no task defined for this case"));
                continue;
            }

            var sessionId = Guid.NewGuid().ToString("N")[..8];
            AnsiConsole.Markup($"  {Markup.Escape(evalCase.Id.PadRight(42))}");

            // Per-case timeout: cancel this case independently of the suite-level token.
            using var caseCts = settings.TimeoutSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            caseCts?.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            var caseToken = caseCts?.Token ?? cancellationToken;

            SessionResult sessionResult;
            TerminationStrategyConfig? termination = null;
            try
            {
                var built = await OrchestratorBuilder.BuildAsync(
                    configPath, loggerFactory, pluginRegistry, approvalService,
                    hitlMode: false, sessionId: sessionId);

                var (orchestrator, config, mcpManager, compactor, changeTracker, eventEmitter,
                     governanceKernel, skillCurator, repoMemoryExtractor, chatClientFactory, _, sessionMetrics, _) = built;
                termination = config.Termination;

                await using var _mcp = mcpManager;
                using  var _gov      = governanceKernel;
                using  var _ccf      = chatClientFactory;

                await ApiKeyValidator.ValidateApiKeysAsync(config);

                var evalStore  = new InMemorySessionStore();
                var checkpoint = new SessionCheckpoint
                {
                    SessionId        = sessionId,
                    Task             = task,
                    ConfigPath       = configPath,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                };
                await evalStore.SaveAsync(checkpoint, caseToken);

                eventEmitter?.SetSessionId(sessionId);
                orchestrator.SetSessionId(sessionId);
                compactor?.SetSessionId(sessionId);
                // Without this, ActiveSessionId stays null on the change log and evidence
                // graph, so EvidenceStore.QueryNodes and the changes.json fallback path both
                // skip session filtering entirely — every contract check (FilesWritten,
                // CommandSucceeded, TestReport.HasAssertions) sees commands and writes from
                // every past eval run against this project, not just the current one. See
                // ChangeTracker.SetSessionIdAsync's doc comment: "so check 8 in TestReportValid
                // filters to only commands recorded in this session, preventing prior-session
                // contamination" — that guarantee silently doesn't hold for `eval run`.
                if (changeTracker is not null)
                    await changeTracker.SetSessionIdAsync(sessionId, caseToken);
                orchestrator.SetStructuredTask(TaskModel.FromGoal(task));

                var runner = new SessionRunner(
                    orchestrator, compactor, evalStore, approvalService,
                    eventEmitter:   null,
                    telemetry:      null,
                    modelIdByAgent: config.Agents.ToDictionary(
                        a => a.Name,
                        a => string.IsNullOrWhiteSpace(a.Model.ModelId) ? "unknown" : a.Model.ModelId,
                        StringComparer.OrdinalIgnoreCase),
                    devUI:          null,
                    configPath:     configPath,
                    maxIterations:  config.Termination?.ResolveMaxIterations() ?? 0,
                    contextBudget:  config.ContextBudget,
                    sessionMetrics: sessionMetrics,
                    quiet:          true);

                sessionResult = await runner.RunAsync(task, checkpoint, hitlMode: false, showTools: false, caseToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Case-level timeout fired; the suite-level token is still live.
                AnsiConsole.MarkupLine("[yellow]TIMEOUT[/]");
                await RecordResultAsync(Failed(evalCase.Id, sessionId, $"timed out after {settings.TimeoutSeconds}s"));
                continue;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[red]ERROR[/]");
                await RecordResultAsync(Failed(evalCase.Id, sessionId, $"orchestrator exception: {ex.Message}", ex.Message));
                continue;
            }

            var caseResult = Score(evalCase, sessionResult, sessionId, termination);
            await RecordResultAsync(caseResult);
            PrintCaseResult(caseResult);
        }

        await WriteStatusAsync(null, "completed");
        if (jsonlWriter is not null)
            await jsonlWriter.DisposeAsync();

        AnsiConsole.WriteLine();
        PrintSummary(results);

        if (settings.OutputPath is not null)
            AnsiConsole.MarkupLine($"[dim]Results → {Markup.Escape(settings.OutputPath)}[/]");

        return settings.Ci && results.Any(r => !r.Passed) ? 1 : 0;
    }

    // ── Scoring ─────────────────────────────────────────────────────────────
    // internal so tests can call directly without spinning up an orchestrator.

    internal static EvalCaseResult Score(
        EvalCase evalCase, SessionResult result, string sessionId, TerminationStrategyConfig? termination = null)
    {
        var failures = new List<string>();

        if (evalCase.MustSucceed && !result.Succeeded)
            failures.Add($"session did not succeed: {result.ErrorMessage ?? "unknown"}");

        // Prefer the last message from whichever agent the orchestration's own regex
        // termination condition is scoped to (Termination.AgentNames) — mirrors
        // RegexTerminationCondition's own agent-filtered backward scan (see its doc
        // comment). Without this, a periodic/auxiliary agent (e.g. a Verifier) that
        // speaks *after* the approving agent's turn becomes "the last assistant message"
        // even though RegexTerminationCondition correctly looked past it and terminated
        // on the earlier, agent-matched message — scoring the session a false FAIL.
        var terminationAgentNames = FindScopedTerminationAgentNames(termination);
        var lastAssistant = terminationAgentNames is { Length: > 0 }
            ? result.Messages.LastOrDefault(m =>
                  m.Role == MessageRole.Assistant &&
                  terminationAgentNames.Any(n => string.Equals(n, m.AgentName, StringComparison.OrdinalIgnoreCase)))
              ?? result.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant)
            : result.Messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
        var finalContent  = lastAssistant?.Content ?? string.Empty;

        // A turn that only calls handoff() with no accompanying prose leaves Content empty —
        // the routing keyword lives in the tool-call argument instead. Fold it in so
        // keyword/regex checks see the same signal RegexTerminationCondition already used
        // to decide the session was done.
        var handoffCall = lastAssistant?.ToolCalls?.LastOrDefault(tc =>
            string.Equals(tc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase));
        if (handoffCall?.ArgsSummary is { Length: > 0 } handoffArgs)
            finalContent = $"{finalContent} {handoffArgs}".Trim();

        foreach (var kw in evalCase.ExpectKeywords)
            if (!finalContent.Contains(kw, StringComparison.OrdinalIgnoreCase))
                failures.Add($"expected keyword not found: \"{kw}\"");

        foreach (var pattern in evalCase.ExpectRegex)
        {
            try
            {
                if (!Regex.IsMatch(finalContent, pattern, RegexOptions.IgnoreCase))
                    failures.Add($"regex not matched: {pattern}");
            }
            catch (ArgumentException)
            {
                failures.Add($"invalid regex pattern: {pattern}");
            }
        }

        foreach (var kw in evalCase.ForbiddenKeywords)
            if (finalContent.Contains(kw, StringComparison.OrdinalIgnoreCase))
                failures.Add($"forbidden keyword found: \"{kw}\"");

        if (evalCase.MaxTurns > 0 && result.Messages.Count > evalCase.MaxTurns)
            failures.Add($"exceeded max_turns: {result.Messages.Count} > {evalCase.MaxTurns}");

        return new EvalCaseResult
        {
            CaseId            = evalCase.Id,
            SessionId         = sessionId,
            Passed            = failures.Count == 0,
            FailureReasons    = failures,
            TotalTurns        = result.Messages.Count,
            DurationMs        = (long)result.Elapsed.TotalMilliseconds,
            TotalInputTokens  = result.Messages.Sum(m => (long)(m.Usage?.InputTokens  ?? 0)),
            TotalOutputTokens = result.Messages.Sum(m => (long)(m.Usage?.OutputTokens ?? 0)),
            ErrorMessage      = result.ErrorMessage,
        };
    }

    // Recursively searches a (possibly composite) termination config for a "regex" or
    // "structured" strategy with AgentNames set, matching how CompositeTerminationStrategy
    // /RegexTerminationCondition/StructuredTerminationCondition are actually built from this
    // same config (see StrategyFactory) — both scan backward with the same agent-filter
    // semantics. Returns the first match depth-first; a config with multiple agent-scoped
    // strategies is not expected here.
    private static string[]? FindScopedTerminationAgentNames(TerminationStrategyConfig? config)
    {
        if (config is null) return null;

        if (config.Type.ToLowerInvariant() is "regex" or "structured"
            && config.AgentNames is { Length: > 0 })
            return config.AgentNames;

        if (config.Strategies is not null)
            foreach (var child in config.Strategies)
                if (FindScopedTerminationAgentNames(child) is { Length: > 0 } found)
                    return found;

        return null;
    }

    internal static EvalSuite LoadSuite(string path)
    {
        var ext     = Path.GetExtension(path).ToLowerInvariant();
        var content = File.ReadAllText(path);

        if (ext is ".yaml" or ".yml")
            return YamlDeserializer.Deserialize<EvalSuite>(content)
                   ?? throw new InvalidDataException("Suite file is empty.");

        return JsonSerializer.Deserialize<EvalSuite>(content, JsonReadOpts)
               ?? throw new InvalidDataException("Suite file is empty.");
    }

    internal static List<EvalCase> ApplyFilter(List<EvalCase> cases, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return cases;
        return cases
            .Where(c =>
                c.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                c.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // ── Display ──────────────────────────────────────────────────────────────

    private static void PrintCaseResult(EvalCaseResult r)
    {
        var icon   = r.Passed ? "[green]PASS[/]" : "[red]FAIL[/]";
        var tokens = r.TotalInputTokens > 0
            ? $"  [dim]in:{r.TotalInputTokens:N0} out:{r.TotalOutputTokens:N0}[/]"
            : string.Empty;

        AnsiConsole.MarkupLine(
            $"{icon}  [dim]{r.TotalTurns} turn(s)  {r.DurationMs:N0}ms{tokens}  {Markup.Escape($"[{r.SessionId}]")}[/]");

        foreach (var reason in r.FailureReasons)
            AnsiConsole.MarkupLine($"    [red]→[/] {Markup.Escape(reason)}");
    }

    private static void PrintSummary(List<EvalCaseResult> results)
    {
        var passed   = results.Count(r => r.Passed);
        var total    = results.Count;
        var color    = passed == total ? "green" : passed == 0 ? "red" : "yellow";
        var totalMs  = results.Sum(r => r.DurationMs);
        var totalIn  = results.Sum(r => r.TotalInputTokens);
        var totalOut = results.Sum(r => r.TotalOutputTokens);

        AnsiConsole.MarkupLine(
            $"[{color}]{passed}/{total} passed[/]" +
            (passed < total ? $"  [red]{total - passed} failed[/]" : string.Empty) +
            $"  [dim]{totalMs:N0}ms total[/]" +
            (totalIn > 0 ? $"  [dim]in:{totalIn:N0} out:{totalOut:N0} tokens[/]" : string.Empty));
    }

    // ── I/O ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Live progress snapshot written to <c>&lt;output&gt;.status.json</c> and overwritten
    /// after every state change (case start, case completion, suite completion) — cheap to
    /// poll from outside the running process without parsing the growing results JSONL.
    /// </summary>
    private sealed record EvalRunStatus(
        string   Suite,
        int      Total,
        int      Completed,
        int      Passed,
        int      Failed,
        string?  CurrentCase,
        string   State,
        DateTime StartedAt,
        DateTime UpdatedAt);

    private static EvalCaseResult Failed(string caseId, string sessionId, string reason, string? error = null) =>
        new()
        {
            CaseId         = caseId,
            SessionId      = sessionId,
            Passed         = false,
            FailureReasons = [reason],
            ErrorMessage   = error,
        };
}
