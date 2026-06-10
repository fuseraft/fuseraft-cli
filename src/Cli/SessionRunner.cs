using System.Diagnostics;
using AgentGovernance.Sre;
using Spectre.Console;
using fuseraft.Cli.DevUI;
using fuseraft.Cli.Display;
using fuseraft.Cli.Telemetry;
using fuseraft.Core.Exceptions;
using fuseraft.Core.Interfaces;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Core.Models;
using fuseraft.Orchestration;
using fuseraft.Orchestration.Strategies;
using MagenticOrchestrator = fuseraft.Orchestration.MagenticOrchestrator;

namespace fuseraft.Cli;

/// <summary>
/// The outcome of a completed session run.
/// </summary>
public sealed record SessionResult(
    bool Succeeded,
    string? ErrorMessage,
    List<AgentMessage> Messages,
    TimeSpan Elapsed);

/// <summary>
/// Runs the main agent streaming loop for a session, handling HITL mode,
/// compaction, and all transient error conditions. Decoupled from CLI setup
/// and post-run rendering so <see cref="Commands.RunCommand"/> stays focused
/// on orchestration wiring and result display.
/// </summary>
public sealed class SessionRunner(
    IOrchestrator orchestrator,
    ConversationCompactor? compactor,
    ISessionStore sessionStore,
    IHumanApprovalService approvalService,
    EventEmitter? eventEmitter,
    FuseraftTelemetry? telemetry,
    IReadOnlyDictionary<string, string> modelIdByAgent,
    DevUIServer? devUI = null,
    string? configPath = null,
    int maxIterations = 0,
    ContextBudgetConfig? contextBudget = null,
    ContextWindowRecorder? contextWindowRecorder = null,
    SessionMetrics? sessionMetrics = null,
    bool quiet = false,
    SnapshotWriter? postmortemWriter = null)
{
    // Session-lifetime assistant-turn counter. Only ever increments — never reset after
    // compaction. Used solely for the MaxIterations hard cap.
    private int _totalAssistantTurnCount;

    private readonly ContextBudgetManager _budgetManager = new(contextBudget, contextWindowRecorder, eventEmitter);
    private readonly CompactionCoordinator _coordinator  = new(
        orchestrator, compactor, sessionStore, eventEmitter, sessionMetrics, contextWindowRecorder,
        sessionId =>
        {
            if (!string.IsNullOrEmpty(configPath))
            {
                var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), configPath);
                return $"fuseraft run --config {rel} --resume {sessionId}";
            }
            return $"fuseraft run --resume {sessionId}";
        });

    // Carrier for the outcome of each exception handler. Avoids out-parameters on async methods.
    private readonly record struct HandlerOutcome(
        bool ShouldBreak,
        bool ShouldContinue,
        bool CompactionNeeded,
        bool Succeeded,
        string? ErrorMessage);

    public async Task<SessionResult> RunAsync(
        string task,
        SessionCheckpoint checkpoint,
        bool hitlMode,
        bool showTools,
        CancellationToken cancellationToken)
    {
        if (devUI is not null)
            orchestrator.AgentStarting += name => devUI.BroadcastAgentStarting(name);

        var messages     = new List<AgentMessage>(checkpoint.Messages);
        var sessionClock = Stopwatch.StartNew();
        var turnClock    = Stopwatch.StartNew();
        var succeeded    = true;
        string? errorMessage = null;
        _totalAssistantTurnCount = messages.Count(m => m.Role == "assistant");

        while (!cancellationToken.IsCancellationRequested)
        {
            string? injection        = null;
            bool    compactionNeeded = false;

            // Pre-turn context size guard: if the retained history already exceeds the
            // per-turn token ceiling, compact before the agent runs. This prevents the
            // agent from spending expensive tokens on a turn that would immediately trigger
            // post-turn compaction anyway. Skipped for the first turn after a compaction
            // (_justCompacted) so we don't thrash when the retained tail itself is large.
            //
            // Estimate uses chars / 3 rather than / 4: code-heavy content (tool results,
            // file reads) averages ~3 chars per token, and the estimate omits tool-schema
            // overhead (~10–20 k tokens for agents with many tools). The conservative
            // divisor compensates for both without needing per-agent schema introspection.
            if (_coordinator.NeedsPreTurnCompaction(checkpoint, contextBudget))
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]  ⚡ Pre-turn context estimate exceeds MaxSingleTurnInputTokens " +
                    $"({contextBudget!.MaxSingleTurnInputTokens:N0}). Compacting before next turn...[/]");
                compactionNeeded = true;
            }

            if (!compactionNeeded)
            try
            {
                if (hitlMode)
                    (injection, compactionNeeded) = await RunHitlIterationAsync(
                        task, checkpoint, messages, turnClock, showTools, cancellationToken);
                else
                    compactionNeeded = await RunSpinnerIterationAsync(
                        task, checkpoint, messages, turnClock, showTools, cancellationToken);
            }
            catch (ValidatorStuckException stuck)
            {
                var outcome = await HandleValidatorStuckAsync(stuck, checkpoint, messages, cancellationToken);
                succeeded    = outcome.Succeeded;
                errorMessage = outcome.ErrorMessage;
                if (outcome.ShouldBreak)    break;
                if (outcome.ShouldContinue) continue;
            }
            catch (CircuitBreakerOpenException cb)
            {
                var outcome = await HandleCircuitBreakerOpenAsync(cb, cancellationToken);
                succeeded    = outcome.Succeeded;
                errorMessage = outcome.ErrorMessage;
                if (outcome.ShouldBreak)    break;
                if (outcome.ShouldContinue) continue;
            }
            catch (BudgetExceededException budget)
            {
                var outcome = await HandleBudgetExceededAsync(budget);
                succeeded    = outcome.Succeeded;
                errorMessage = outcome.ErrorMessage;
                if (outcome.ShouldBreak) break;
            }
            catch (TimeoutException tex)
            {
                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("hitl_escalation",
                        payload: new { reason = "streaming_timeout", message = tex.Message });

                AnsiConsole.MarkupLine(
                    $"\n[yellow]⚠ Streaming timeout.[/]\n" +
                    $"  {Markup.Escape(tex.Message)}\n");

                var redirect = await approvalService.PromptRedirectAsync("(streaming-timeout)");
                if (redirect == null)
                {
                    succeeded    = false;
                    errorMessage = $"Aborted: streaming timeout — {tex.Message}";
                    AnsiConsole.MarkupLine(
                        $"\n[yellow]Session [bold]{checkpoint.SessionId}[/] paused — resume with:[/] " +
                        $"[dim]{Markup.Escape(ResumeHint(checkpoint.SessionId))}[/]");
                    break;
                }

                await InjectAndSaveHumanMessageAsync(redirect, messages, checkpoint, cancellationToken);
                continue;
            }
            catch (OperationCanceledException)
            {
                succeeded    = false;
                errorMessage = "Cancelled.";
                AnsiConsole.MarkupLine(
                    $"\n[yellow]Session [bold]{checkpoint.SessionId}[/] paused — resume with:[/] " +
                    $"[dim]{Markup.Escape(ResumeHint(checkpoint.SessionId))}[/]");
                break;
            }
            catch (Exception ex) when (Is429(ex))
            {
                var outcome = await HandleRateLimitAsync(ex, checkpoint);
                succeeded    = outcome.Succeeded;
                errorMessage = outcome.ErrorMessage;
                if (outcome.ShouldBreak) break;
            }
            catch (Exception ex) when (ProviderErrorClassifier.Classify(ex) == FailoverReason.ContextExceeded && compactor is not null)
            {
                var outcome = await HandleContextExceededAsync(ex, checkpoint, withCompactor: true);
                compactionNeeded = outcome.CompactionNeeded;
                succeeded    = outcome.Succeeded;
                errorMessage = outcome.ErrorMessage;
                if (outcome.ShouldBreak) break;
            }
            catch (Exception ex) when (ProviderErrorClassifier.Classify(ex) == FailoverReason.ContextExceeded)
            {
                var outcome = await HandleContextExceededAsync(ex, checkpoint, withCompactor: false);
                succeeded    = outcome.Succeeded;
                errorMessage = outcome.ErrorMessage;
                if (outcome.ShouldBreak) break;
            }
            catch (Exception ex) when (Is400(ex) && ProviderErrorClassifier.Classify(ex) == FailoverReason.None)
            {
                var outcome = await HandleHttpBadRequestAsync(ex, checkpoint, messages, cancellationToken);
                succeeded    = outcome.Succeeded;
                errorMessage = outcome.ErrorMessage;
                if (outcome.ShouldBreak)    break;
                if (outcome.ShouldContinue) continue;
            }
            catch (Exception ex)
            {
                var outcome = await HandleSessionFaultAsync(ex, checkpoint);
                succeeded    = outcome.Succeeded;
                errorMessage = outcome.ErrorMessage;
                if (outcome.ShouldBreak) break;
            }

            if (cancellationToken.IsCancellationRequested) break;

            // Session-level hard cap. Count only agent (assistant) turns across all StreamAsync
            // invocations. This fires even when compaction resets the internal phase counter.
            if (maxIterations > 0 && _totalAssistantTurnCount >= maxIterations)
            {
                succeeded    = false;
                errorMessage = $"Session exceeded MaxIterations limit of {maxIterations} agent turns.";
                AnsiConsole.MarkupLine(
                    $"\n[yellow]⚠ MaxIterations ({maxIterations}) reached — session terminated.[/]\n" +
                    $"  Resume with: [dim]{Markup.Escape(ResumeHint(checkpoint.SessionId))}[/]");
                break;
            }

            if (compactionNeeded)
            {
                var (updatedCheckpoint, shouldBreak, shouldContinue, compactionError) =
                    await _coordinator.TryTriggerCompactionAsync(task, checkpoint, _totalAssistantTurnCount, _budgetManager, cancellationToken);
                checkpoint = updatedCheckpoint;
                if (shouldBreak)
                {
                    succeeded    = false;
                    errorMessage = compactionError;
                    break;
                }
                if (shouldContinue) continue;
            }

            // Non-null, non-quit injection: the HITL user typed a redirect message.
            if (injection is not null && injection != "\x00")
            {
                await InjectAndSaveHumanMessageAsync(injection, messages, checkpoint, cancellationToken);
                continue;
            }

            // Normal completion or HITL quit — exit the loop.
            break;
        }

        sessionClock.Stop();

        await FinalizeSessionAsync(succeeded, errorMessage, task, sessionClock.Elapsed, checkpoint);

        return new SessionResult(succeeded, errorMessage, messages, sessionClock.Elapsed);
    }

    // Returns the resume command string, including --config when a config path is known.
    private string ResumeHint(string sessionId)
    {
        if (!string.IsNullOrEmpty(configPath))
        {
            var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), configPath);
            return $"fuseraft run --config {rel} --resume {sessionId}";
        }
        return $"fuseraft run --resume {sessionId}";
    }

    // ── Exception handlers ────────────────────────────────────────────────────

    private async Task<HandlerOutcome> HandleValidatorStuckAsync(
        ValidatorStuckException stuck,
        SessionCheckpoint checkpoint,
        List<AgentMessage> messages,
        CancellationToken cancellationToken)
    {
        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("hitl_escalation",
                agent: stuck.AgentName,
                payload: new { validator = stuck.ValidatorName, consecutive_failures = stuck.ConsecutiveFailures, last_error = stuck.LastValidatorError });

        AnsiConsole.MarkupLine(
            $"\n[yellow]⚠ HITL intervention required.[/]\n" +
            $"  Agent:    [bold]{Markup.Escape(stuck.AgentName)}[/]\n" +
            $"  Blocked:  [bold]{Markup.Escape(stuck.ValidatorName)}[/] " +
            $"({stuck.ConsecutiveFailures} consecutive failures)\n" +
            $"  Last error:\n[dim]{Markup.Escape(stuck.LastValidatorError)}[/]\n");

        var redirect = await approvalService.PromptRedirectAsync(stuck.AgentName);

        if (redirect == null)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]Session [bold]{checkpoint.SessionId}[/] paused — resume with:[/] " +
                $"[dim]{Markup.Escape(ResumeHint(checkpoint.SessionId))}[/]");
            return new HandlerOutcome(ShouldBreak: true, ShouldContinue: false, CompactionNeeded: false,
                Succeeded: false, ErrorMessage: $"Aborted: agent '{stuck.AgentName}' stuck on validator '{stuck.ValidatorName}'.");
        }

        await InjectAndSaveHumanMessageAsync(redirect, messages, checkpoint, cancellationToken);
        return new HandlerOutcome(ShouldBreak: false, ShouldContinue: true, CompactionNeeded: false,
            Succeeded: true, ErrorMessage: null);
    }

    private async Task<HandlerOutcome> HandleCircuitBreakerOpenAsync(
        CircuitBreakerOpenException cb,
        CancellationToken cancellationToken)
    {
        const int MaxAutoRetrySeconds = 300;
        if (!cancellationToken.IsCancellationRequested && cb.RetryAfter.TotalSeconds <= MaxAutoRetrySeconds)
        {
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("circuit_breaker_open",
                    payload: new { retry_after_seconds = cb.RetryAfter.TotalSeconds });
            var wait = cb.RetryAfter + TimeSpan.FromSeconds(2);
            AnsiConsole.MarkupLine(
                $"\n[yellow]⚠ Circuit breaker open[/] — waiting {wait.TotalSeconds:F0}s for it to reset...[/]");
            await Task.Delay(wait, cancellationToken);
            AnsiConsole.MarkupLine("[dim]Retrying...[/]");
            return new HandlerOutcome(ShouldBreak: false, ShouldContinue: true, CompactionNeeded: false,
                Succeeded: true, ErrorMessage: null);
        }

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("session_error",
                payload: new { reason = "circuit_breaker_open", retry_after_seconds = cb.RetryAfter.TotalSeconds });
        AnsiConsole.MarkupLine(
            $"\n[red]✗ Circuit breaker open:[/] Too many consecutive LLM failures. " +
            $"[dim]Retry after {cb.RetryAfter.TotalSeconds:F0}s.[/]\n");
        return new HandlerOutcome(ShouldBreak: true, ShouldContinue: false, CompactionNeeded: false,
            Succeeded: false, ErrorMessage: $"Circuit breaker open — LLM calls failing. Retry after {cb.RetryAfter.TotalSeconds:F0}s.");
    }

    private async Task<HandlerOutcome> HandleBudgetExceededAsync(BudgetExceededException budget)
    {
        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("session_error",
                payload: new { reason = "token_budget_exceeded", actual_tokens = budget.ActualTokens, limit_tokens = budget.LimitTokens });
        AnsiConsole.MarkupLine(
            $"\n[red]✗ Error:[/] Session used [bold]{budget.ActualTokens:N0}[/] tokens, " +
            $"exceeding the configured budget of [bold]{budget.LimitTokens:N0}[/].\n");
        return new HandlerOutcome(ShouldBreak: true, ShouldContinue: false, CompactionNeeded: false,
            Succeeded: false, ErrorMessage: budget.Message);
    }

    private async Task<HandlerOutcome> HandleRateLimitAsync(Exception ex, SessionCheckpoint checkpoint)
    {
        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("session_error",
                payload: new { reason = "rate_limited_429", message = ex.Message });
        AnsiConsole.MarkupLine(
            $"\n[red]✗ API rate limit / quota exceeded (HTTP 429)[/]\n" +
            $"  [dim]{Markup.Escape(TrimTo(ex.Message, 300))}[/]\n" +
            $"\n[yellow]Session [bold]{checkpoint.SessionId}[/] saved — resume once credits are restored:[/] " +
            $"[dim]{Markup.Escape(ResumeHint(checkpoint.SessionId))}[/]");
        return new HandlerOutcome(ShouldBreak: true, ShouldContinue: false, CompactionNeeded: false,
            Succeeded: false, ErrorMessage: ex.Message);
    }

    private async Task<HandlerOutcome> HandleContextExceededAsync(
        Exception ex,
        SessionCheckpoint checkpoint,
        bool withCompactor)
    {
        if (withCompactor)
        {
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("context_exceeded_recovery",
                    payload: new { message = TrimTo(ex.Message, 200) });
            AnsiConsole.MarkupLine(
                $"\n[yellow]⚠ Context window exceeded — fallover chain exhausted.[/] Compacting history and retrying...\n" +
                $"  [dim]{Markup.Escape(TrimTo(ex.Message, 200))}[/]\n");
            _coordinator.SetPendingReason(CompactionReason.ContextExceeded);
            return new HandlerOutcome(ShouldBreak: false, ShouldContinue: false, CompactionNeeded: true,
                Succeeded: true, ErrorMessage: null);
        }

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("session_error",
                payload: new { reason = "context_exceeded_no_compactor", message = TrimTo(ex.Message, 200) });
        AnsiConsole.MarkupLine(
            $"\n[red]✗ Context window exceeded[/] — no compactor configured.\n" +
            $"  Add [dim]compaction: window[/] (or [dim]llm[/]) to your config to enable auto-compaction.\n" +
            $"\n[yellow]Session [bold]{checkpoint.SessionId}[/] saved — resume after adding compaction config:[/] " +
            $"[dim]{Markup.Escape(ResumeHint(checkpoint.SessionId))}[/]");
        return new HandlerOutcome(ShouldBreak: true, ShouldContinue: false, CompactionNeeded: false,
            Succeeded: false, ErrorMessage: "Context window exceeded with no compaction configured.");
    }

    private async Task<HandlerOutcome> HandleHttpBadRequestAsync(
        Exception ex,
        SessionCheckpoint checkpoint,
        List<AgentMessage> messages,
        CancellationToken cancellationToken)
    {
        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("hitl_escalation",
                payload: new { reason = "provider_400", message = TrimTo(ex.Message, 200) });
        AnsiConsole.MarkupLine(
            $"\n[yellow]⚠ Provider returned HTTP 400 (bad request).[/]\n" +
            $"  [dim]{Markup.Escape(TrimTo(ex.Message, 300))}[/]\n");
        var redirect = await approvalService.PromptRedirectAsync("(provider-400)");
        if (redirect == null)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]Session [bold]{checkpoint.SessionId}[/] paused — resume with:[/] " +
                $"[dim]{Markup.Escape(ResumeHint(checkpoint.SessionId))}[/]");
            return new HandlerOutcome(ShouldBreak: true, ShouldContinue: false, CompactionNeeded: false,
                Succeeded: false, ErrorMessage: $"Aborted: provider 400 — {TrimTo(ex.Message, 200)}");
        }
        await InjectAndSaveHumanMessageAsync(redirect, messages, checkpoint, cancellationToken);
        return new HandlerOutcome(ShouldBreak: false, ShouldContinue: true, CompactionNeeded: false,
            Succeeded: true, ErrorMessage: null);
    }

    private Task<HandlerOutcome> HandleSessionFaultAsync(Exception ex, SessionCheckpoint checkpoint)
    {
        string? dumpPath = null;
        try { dumpPath = CrashDumper.Write(ex, []); } catch { }
        AnsiConsole.MarkupLine(
            $"\n[red]✗ Unexpected error:[/] {Markup.Escape(TrimTo(ex.Message, 300))}");
        if (dumpPath is not null)
            AnsiConsole.MarkupLine($"  [dim]Crash dump: {Markup.Escape(dumpPath)}[/]");
        AnsiConsole.MarkupLine(
            $"\n[yellow]Session [bold]{checkpoint.SessionId}[/] saved — resume with:[/] " +
            $"[dim]{Markup.Escape(ResumeHint(checkpoint.SessionId))}[/]");
        return Task.FromResult(new HandlerOutcome(ShouldBreak: true, ShouldContinue: false, CompactionNeeded: false,
            Succeeded: false, ErrorMessage: ex.Message));
    }

    // ── Session finalization ──────────────────────────────────────────────────

    private async Task FinalizeSessionAsync(
        bool succeeded,
        string? errorMessage,
        string task,
        TimeSpan elapsed,
        SessionCheckpoint checkpoint)
    {
        if (sessionMetrics is not null)
            try { await sessionMetrics.PrintSummaryAsync(eventEmitter, checkpoint.SessionId); } catch { }

        if (postmortemWriter is not null)
            try { await postmortemWriter.WriteManifestAsync(succeeded, errorMessage, task, elapsed); } catch { }
    }

    // Iteration helpers

    private async Task<(string? Injection, bool CompactionNeeded)> RunHitlIterationAsync(
        string task,
        SessionCheckpoint checkpoint,
        List<AgentMessage> messages,
        Stopwatch turnClock,
        bool showTools,
        CancellationToken cancellationToken)
    {
        string? injection        = null;
        bool    compactionNeeded = false;
        bool    lastWasEnter     = false;

        Action<string, string, string?> onToolCalling = (_, tool, args) =>
        {
            var line = args is not null ? $"  ❯ {tool}({args})" : $"  ❯ {tool}()";
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(line)}[/]");
        };

        Action<string, int, int> onTokenBudgetWarning = (agent, inputTokens, threshold) =>
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  ⚠ {Markup.Escape(agent)} used {inputTokens:N0} input tokens this turn " +
                $"(warning threshold: {threshold:N0}). " +
                $"Reduce file reads and shell output to avoid a budget blowup.[/]");
        };

        orchestrator.ToolCalling        += onToolCalling;
        orchestrator.TokenBudgetWarning += onTokenBudgetWarning;

        try
        {

        await foreach (var msg in orchestrator.StreamAsync(task, checkpoint.Messages, cancellationToken))
        {
            var elapsed = turnClock.Elapsed;
            turnClock.Restart();

            MessageRenderer.RenderMessage(msg, elapsed, showTools);
            try { telemetry?.RecordTurn(msg, elapsed, modelIdByAgent.GetValueOrDefault(msg.AgentName)); } catch { }
            try { devUI?.BroadcastMessage(msg, elapsed); } catch { }
            if (await RecordMessageAsync(msg, messages, checkpoint, cancellationToken))
            {
                compactionNeeded = true;
                lastWasEnter = false;
                break;
            }

            injection = await approvalService.PromptContinueAsync();
            if (injection == null)
            {
                lastWasEnter = true;
                continue;
            }
            lastWasEnter = false;
            break;
        }

        bool streamCompleted = lastWasEnter && !compactionNeeded;

        if (streamCompleted && !cancellationToken.IsCancellationRequested)
            injection = await approvalService.PromptPostSessionAsync();

        } // end try
        finally
        {
            orchestrator.ToolCalling        -= onToolCalling;
            orchestrator.TokenBudgetWarning -= onTokenBudgetWarning;
        }

        return (injection, compactionNeeded);
    }

    private async Task<bool> RunSpinnerIterationAsync(
        string task,
        SessionCheckpoint checkpoint,
        List<AgentMessage> messages,
        Stopwatch turnClock,
        bool showTools,
        CancellationToken cancellationToken)
    {
        bool compactionNeeded = false;

        if (quiet)
        {
            compactionNeeded = await RunStreamCoreAsync(
                task, checkpoint, messages, turnClock, showTools,
                statusUpdate: null, cancellationToken);
        }
        else
        {
            await AnsiConsole.Status()
                .Spinner(OperatingSystem.IsWindows() ? Spinner.Known.Line : Spinner.Known.Dots2)
                .SpinnerStyle(Style.Parse("dim"))
                .StartAsync("[dim]Starting orchestration...[/]", async ctx =>
                {
                    compactionNeeded = await RunStreamCoreAsync(
                        task, checkpoint, messages, turnClock, showTools,
                        statusUpdate: s => ctx.Status(s), cancellationToken);
                });
        }

        return compactionNeeded;
    }

    // Stream loop shared by quiet and interactive modes.
    // statusUpdate is null in quiet mode — suppresses the spinner, turn panels, and budget warnings.
    private async Task<bool> RunStreamCoreAsync(
        string task,
        SessionCheckpoint checkpoint,
        List<AgentMessage> messages,
        Stopwatch turnClock,
        bool showTools,
        Action<string>? statusUpdate,
        CancellationToken cancellationToken)
    {
        bool compactionNeeded = false;

        // Store handler refs so we can unsubscribe after the stream ends.
        // Without this, every compaction cycle adds another copy of each handler,
        // causing warnings and status updates to fire N times by turn N.
        Action<string> onAgentStarting = name =>
            statusUpdate?.Invoke($"[dim]{Markup.Escape(name)} thinking...[/]");

        Action<string, string, string?> onToolCalling = (agent, tool, args) =>
        {
            var raw = args is not null
                ? $"{agent}: {tool}({args})"
                : $"{agent}: {tool}()";

            var available = AnsiConsole.Console.Profile.Width - 2;
            if (available > 0 && raw.Length > available)
                raw = raw[..(available - 1)] + "…";

            statusUpdate?.Invoke($"[dim]{Markup.Escape(raw)}[/]");
        };

        Action<string, int, int> onTokenBudgetWarning = (agent, inputTokens, threshold) =>
        {
            if (statusUpdate is not null)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(
                    $"[yellow]  ⚠ {Markup.Escape(agent)} used {inputTokens:N0} input tokens this turn " +
                    $"(warning threshold: {threshold:N0}). " +
                    $"Reduce file reads and shell output to avoid a budget blowup.[/]");
                AnsiConsole.WriteLine();
            }
        };

        orchestrator.AgentStarting      += onAgentStarting;
        orchestrator.ToolCalling        += onToolCalling;
        orchestrator.TokenBudgetWarning += onTokenBudgetWarning;

        try
        {
            await foreach (var msg in orchestrator.StreamAsync(task, checkpoint.Messages, cancellationToken))
            {
                var elapsed = turnClock.Elapsed;
                turnClock.Restart();

                // Orchestrator-injected correction messages (AgentName="orchestrator", Role="user")
                // are persisted to checkpoint for resume but should not update the status spinner
                // or appear in the rendered display — they are internal routing signals.
                bool isOrchestratorMessage = msg.AgentName == "orchestrator";

                if (!isOrchestratorMessage)
                {
                    statusUpdate?.Invoke($"[dim]{Markup.Escape(msg.AgentName)} thinking...[/]");
                    if (statusUpdate is not null)
                        MessageRenderer.RenderMessage(msg, elapsed, showTools);
                }

                try { telemetry?.RecordTurn(msg, elapsed, modelIdByAgent.GetValueOrDefault(msg.AgentName)); } catch { }
                try { devUI?.BroadcastMessage(msg, elapsed); } catch { }
                if (await RecordMessageAsync(msg, messages, checkpoint, cancellationToken, statusActive: statusUpdate is not null))
                {
                    compactionNeeded = true;
                    break;
                }
            }
        }
        finally
        {
            orchestrator.AgentStarting      -= onAgentStarting;
            orchestrator.ToolCalling        -= onToolCalling;
            orchestrator.TokenBudgetWarning -= onTokenBudgetWarning;
        }

        return compactionNeeded;
    }

    private async Task<bool> RecordMessageAsync(
        AgentMessage msg,
        List<AgentMessage> messages,
        SessionCheckpoint checkpoint,
        CancellationToken ct,
        bool statusActive = false)
    {
        messages.Add(msg);
        checkpoint.Messages.Add(msg);
        if (msg.Role == "assistant")
        {
            _totalAssistantTurnCount++;
            sessionMetrics?.RecordTurn(msg);
        }
        checkpoint.LastUpdatedAt = DateTime.UtcNow;
        if (postmortemWriter is not null)
            try { await postmortemWriter.RecordTurnAsync(msg); } catch { }
        if (orchestrator is MagenticOrchestrator mo) checkpoint.MagenticState = mo.CurrentState;
        if (orchestrator is GraphOrchestrator go) checkpoint.StateHistory = [..go.StateHistory];
        try
        {
            await sessionStore.SaveAsync(checkpoint, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception saveEx)
        {
            if (statusActive) AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[yellow]  ⚠ Checkpoint save failed: {Markup.Escape(TrimTo(saveEx.Message, 200))}[/]");
        }

        var budgetResult = await _budgetManager.EvaluateAsync(msg, statusActive);
        return await _coordinator.EvaluateCompactionTriggerAsync(checkpoint, msg, budgetResult, statusActive);
    }

    private async Task InjectAndSaveHumanMessageAsync(
        string content,
        List<AgentMessage> messages,
        SessionCheckpoint checkpoint,
        CancellationToken ct)
    {
        var msg = HumanMessage(content, messages.Count);
        checkpoint.Messages.Add(msg);
        messages.Add(msg);
        MessageRenderer.RenderHumanMessage(msg);
        await sessionStore.SaveAsync(checkpoint, ct);
    }

    private static AgentMessage HumanMessage(string content, int turnIndex) => new()
    {
        AgentName = "Human",
        Content   = content,
        Role      = "user",
        TurnIndex = turnIndex,
    };

    // Returns true when the exception (or any inner exception) is an HTTP 400.
    private static bool Is400(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.GetType().Name == "ClientResultException")
            {
                var status = e.GetType().GetProperty("Status")?.GetValue(e);
                if (status is int code && code == 400) return true;
            }
            if (e is System.Net.Http.HttpRequestException httpEx &&
                httpEx.StatusCode == System.Net.HttpStatusCode.BadRequest)
                return true;
        }
        return false;
    }

    // Returns true when the exception (or any inner exception) is an HTTP 429.
    private static bool Is429(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message;
            if (msg.Contains("429", StringComparison.Ordinal) ||
                msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("spending limit", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("used all available credits", StringComparison.OrdinalIgnoreCase))
                return true;
            if (e.GetType().Name == "ClientResultException")
            {
                var status = e.GetType().GetProperty("Status")?.GetValue(e);
                if (status is int code && code == 429) return true;
            }
        }
        return false;
    }

    private static string TrimTo(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
