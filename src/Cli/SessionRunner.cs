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

// Compaction trigger classification — informs the session_summary event and the
// compaction event reason field so post-session analysis can identify the primary
// cause of each compaction cycle.
file static class CompactionReason
{
    public const string SingleTurnLimit = "single_turn_limit";
    public const string CumulativeBudget = "cumulative_budget";
    public const string ShouldCompact = "window_size";
    public const string AgentRequested = "agent_requested";
    public const string ContextExceeded = "context_exceeded";
}

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
    private readonly Dictionary<string, int> _perAgentCumulativeInputTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedAgents = new(StringComparer.OrdinalIgnoreCase);

    // Reason for the pending compaction cycle — set just before compactionNeeded=true,
    // cleared after the compaction event is emitted. Informs session_summary and the
    // compaction event reason field so post-hoc analysis can identify the cause.
    private string _pendingCompactionReason = CompactionReason.ShouldCompact;

    // Set to true after each compaction cycle. Suppresses CutoverAt (cumulative) enforcement
    // for exactly one turn so a post-compaction turn can run without immediately triggering
    // another compaction — the history is already at minimum after compaction and re-compacting
    // before the agent makes any progress would thrash indefinitely.
    // MaxSingleTurnInputTokens is NOT suppressed: a single-turn explosion should always trigger
    // compaction regardless of whether we just compacted, since the compaction summary itself
    // may be large enough to start the next turn already over the per-turn limit.
    private bool _justCompacted;

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
            if (!_justCompacted
                && compactor is not null
                && contextBudget?.MaxSingleTurnInputTokens > 0
                && checkpoint.Messages.Sum(m => (m.Content?.Length ?? 0) / 3) > contextBudget.MaxSingleTurnInputTokens)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]  ⚡ Pre-turn context estimate exceeds MaxSingleTurnInputTokens " +
                    $"({contextBudget.MaxSingleTurnInputTokens:N0}). Compacting before next turn...[/]");
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
                    await TryTriggerCompactionAsync(task, checkpoint, cancellationToken);
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
            _pendingCompactionReason = CompactionReason.ContextExceeded;
            return new HandlerOutcome(ShouldBreak: false, ShouldContinue: false, CompactionNeeded: true,
                Succeeded: true, ErrorMessage: null);
        }

        // Compactor is not configured — nothing we can do but surface a clear message.
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

    // ── Compaction trigger ────────────────────────────────────────────────────

    private async Task<(SessionCheckpoint Checkpoint, bool ShouldBreak, bool ShouldContinue, string? ErrorMessage)> TryTriggerCompactionAsync(
        string task,
        SessionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            checkpoint = await ApplyCompactionAsync(task, checkpoint, compactor!, cancellationToken);

            PostCompactionReset(checkpoint);
            if (contextWindowRecorder is not null)
                await contextWindowRecorder.RecordCompactionAsync(_totalAssistantTurnCount);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]Session [bold]{checkpoint.SessionId}[/] paused — resume with:[/] " +
                $"[dim]{Markup.Escape(ResumeHint(checkpoint.SessionId))}[/]");
            return (checkpoint, ShouldBreak: true, ShouldContinue: false, ErrorMessage: "Cancelled.");
        }
        catch (Exception ex)
        {
            // Compaction itself failed. Treat as a session error rather than letting
            // the exception escape RunAsync to the caller uncaught.
            string? dumpPath = null;
            try { dumpPath = CrashDumper.Write(ex, []); } catch { }
            AnsiConsole.MarkupLine(
                $"\n[red]✗ Compaction error:[/] {Markup.Escape(TrimTo(ex.Message, 300))}");
            if (dumpPath is not null)
                AnsiConsole.MarkupLine($"  [dim]Crash dump: {Markup.Escape(dumpPath)}[/]");
            AnsiConsole.MarkupLine(
                $"\n[yellow]Session [bold]{checkpoint.SessionId}[/] saved — resume with:[/] " +
                $"[dim]{Markup.Escape(ResumeHint(checkpoint.SessionId))}[/]");
            return (checkpoint, ShouldBreak: true, ShouldContinue: false, ErrorMessage: $"Compaction failed: {ex.Message}");
        }

        if (checkpoint.ResumeExecutorId is not null)
            orchestrator.SetResumeExecutorId(checkpoint.ResumeExecutorId);
        if (checkpoint.CurrentStateName is not null)
            orchestrator.SetResumeStateName(checkpoint.CurrentStateName);

        // Restore failure-tracking counters for the state machine so MaxConsecutiveContractFailures
        // and the REPLAN BLOCKED guard survive across compaction cycles.
        if (orchestrator is AgentOrchestrator ao && checkpoint.StateMachineState is { } smState)
            ao.SetResumeSnapshot(smState);

        // Restore Magentic loop-counter state so the next StreamAsync call resumes at
        // the correct round/stall/reset counts rather than restarting from zero.
        if (orchestrator is MagenticOrchestrator magentic && checkpoint.MagenticState is { } magState)
            magentic.SetResumeState(magState);

        AnsiConsole.MarkupLine("[dim]History compacted — continuing session.[/]");
        return (checkpoint, ShouldBreak: false, ShouldContinue: true, ErrorMessage: null);
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
        bool    lastWasEnter     = false;   // tracks whether the last user action was Enter (vs redirect/break)

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
                continue;       // Enter — keep streaming
            }
            lastWasEnter = false;
            break;              // redirect or quit — exit foreach
        }

        // streamCompleted is true only when the stream drained naturally after the user pressed
        // Enter on the last message — not when the user redirected, compaction fired, or an
        // exception propagated out of the loop.
        bool streamCompleted = lastWasEnter && !compactionNeeded;

        // When the stream ended because the termination condition (or max iterations) fired,
        // show a clear "session complete" prompt instead of silently exiting.  The user may
        // want to send a follow-up message and keep the session alive.
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
            var status = args is not null
                ? $"[dim]{Markup.Escape(agent)}: {Markup.Escape(tool)}({Markup.Escape(args)})[/]"
                : $"[dim]{Markup.Escape(agent)}: {Markup.Escape(tool)}()[/]";
            statusUpdate?.Invoke(status);
        };

        Action<string, int, int> onTokenBudgetWarning = (agent, inputTokens, threshold) =>
        {
            // statusUpdate?.Invoke($"[yellow]{Markup.Escape(agent)} thinking...[/]");
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

        orchestrator.AgentStarting     += onAgentStarting;
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
            orchestrator.AgentStarting     -= onAgentStarting;
            orchestrator.ToolCalling        -= onToolCalling;
            orchestrator.TokenBudgetWarning -= onTokenBudgetWarning;
        }

        return compactionNeeded;
    }

    private async Task<SessionCheckpoint> ApplyCompactionAsync(
        string task,
        SessionCheckpoint checkpoint,
        ConversationCompactor compactor,
        CancellationToken cancellationToken)
    {
        // Capture which executor is active before throwing away the full history so the
        // next StreamAsync starts from the correct agent (not the default Planner).
        // Skip for Magentic: SetResumeExecutorId is a no-op there, and the last assistant
        // message in a Magentic session is often a manager tag like "[MagenticManager:Final]"
        // which would write a misleading executor ID into the checkpoint.
        // Capture which executor is active before discarding full history so the next
        // StreamAsync starts from the correct agent. Skip for Magentic.
        string? lastAssistantAgent = null;
        if (orchestrator is not MagenticOrchestrator)
        {
            lastAssistantAgent = checkpoint.Messages
                .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.AgentName))
                ?.AgentName
                ?.ToLowerInvariant();

            checkpoint.ResumeExecutorId = lastAssistantAgent;
        }

        string modifiedFilesNote = BuildModifiedFilesNote(checkpoint.Messages);

        // Capture the current snapshotter from the orchestrator (non-null only for state machine sessions).
        var snapshotter = (orchestrator as AgentOrchestrator)?.CurrentSnapshotter;

        // Capture the state machine's current state so post-compaction StreamAsync calls
        // restore to e.g. "Testing" rather than resetting to the initial "Planning" state.
        // Also capture failure-tracking counters so MaxConsecutiveContractFailures and the
        // REPLAN BLOCKED guard survive across compaction cycles.
        if (snapshotter is not null)
        {
            try
            {
                var snap = await snapshotter.SnapshotAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(snap.CurrentStateName))
                    checkpoint.CurrentStateName = snap.CurrentStateName;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Debug.WriteLine($"[SessionRunner] state snapshot failed: {ex.Message}"); }
        }

        if (snapshotter is StateMachineSelectionStrategy smStrategy)
        {
            try { checkpoint.StateMachineState = smStrategy.TakeCheckpointState(); }
            catch (Exception ex) { Debug.WriteLine($"[SessionRunner] failure-state capture failed: {ex.Message}"); }
        }

        // Diagnostic (Phase 5): log both the last-message agent and the state machine's
        // current state so post-hoc analysis can confirm whether the wrong agent is resumed
        // after a handoff-then-compaction sequence.
        if (orchestrator is not MagenticOrchestrator && eventEmitter is not null)
            _ = eventEmitter.EmitAsync("compaction_resume_candidate",
                payload: new
                {
                    last_assistant_agent = lastAssistantAgent,
                    current_state_name   = checkpoint.CurrentStateName,
                    reason               = _pendingCompactionReason,
                    total_messages       = checkpoint.Messages.Count,
                });

        int turnsBefore = checkpoint.Messages.Count;

        // Snapshot original messages before any trimming — needed for signal pinning.
        var originalMessages = compactor.Config.PinLastRoutingSignal
            ? (IReadOnlyList<AgentMessage>)checkpoint.Messages.ToList()
            : null;

        if (compactor.IsWindowMode)
        {
            var trimmed = compactor.TrimToWindow(checkpoint.Messages);
            int dropped = turnsBefore - trimmed.Count;

            checkpoint.Messages.Clear();
            checkpoint.Messages.AddRange(trimmed);

            if (originalMessages is not null)
                TryPinLastRoutingSignal(checkpoint.Messages, originalMessages);

            checkpoint.LastUpdatedAt = DateTime.UtcNow;

            sessionMetrics?.RecordCompaction(_pendingCompactionReason);
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("compaction",
                    payload: new
                    {
                        mode            = "window",
                        reason          = _pendingCompactionReason,
                        turns_dropped   = dropped,
                        turns_retained  = trimmed.Count,
                        resume_from     = checkpoint.ResumeExecutorId ?? "planner"
                    });

            await sessionStore.SaveAsync(checkpoint, cancellationToken);
            return checkpoint;
        }

        if (checkpoint.Messages.Count < 2)
        {
            AnsiConsole.MarkupLine("[yellow]  Compaction skipped: fewer than 2 messages in history — nothing to compact.[/]");
            return checkpoint;
        }

        var (summary, retained) = await compactor.CompactAsync(task, checkpoint.Messages, cancellationToken, snapshotter);

        if (modifiedFilesNote.Length > 0)
            summary = summary with { Content = summary.Content + modifiedFilesNote };

        checkpoint.Messages.Clear();
        checkpoint.Messages.Add(summary);
        checkpoint.Messages.AddRange(retained);

        if (originalMessages is not null)
            TryPinLastRoutingSignal(checkpoint.Messages, originalMessages);

        checkpoint.LastUpdatedAt = DateTime.UtcNow;

        sessionMetrics?.RecordCompaction(_pendingCompactionReason);
        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("compaction",
                payload: new
                {
                    turns_compacted = turnsBefore - retained.Count,
                    turns_retained  = retained.Count,
                    reason          = _pendingCompactionReason,
                    resume_from     = checkpoint.ResumeExecutorId ?? "planner"
                });

        await sessionStore.SaveAsync(checkpoint, cancellationToken);
        return checkpoint;
    }

    // Re-injects the last handoff signal at the head of the retained window if it was
    // dropped by compaction. Prevents keyword_not_found re-invocations on the first turn
    // after compaction when the signal fell outside the retained tail.
    // The synthetic AgentMessage uses Role="user" so it is replayed as a ChatMessage that
    // IsSignalOnOwnLine can match — it does NOT start with "[fuseraft:" so TransitionAlreadyFired
    // treats it as unprocessed.
    private static void TryPinLastRoutingSignal(
        List<AgentMessage> retained,
        IReadOnlyList<AgentMessage> original)
    {
        // Find the last handoff in the pre-compaction history.
        AgentMessage? lastHandoff = null;
        for (int i = original.Count - 1; i >= 0; i--)
        {
            var m = original[i];
            if (m.Role == "assistant" &&
                m.ToolCalls?.Any(tc => string.Equals(tc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase)) == true)
            {
                lastHandoff = m;
                break;
            }
        }
        if (lastHandoff is null) return;

        // Extract route_keyword from ArgsSummary: "route_keyword=SOME KEYWORD"
        var handoffCall = lastHandoff.ToolCalls!.First(tc =>
            string.Equals(tc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase));
        var argsSummary = handoffCall.ArgsSummary;
        if (argsSummary is null) return;

        var prefix = $"{HandoffPlugin.ArgumentName}=";
        var routeKeyword = argsSummary.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? argsSummary[prefix.Length..].Trim()
            : null;
        if (string.IsNullOrEmpty(routeKeyword)) return;

        // Skip if the signal already survived into the retained window.
        bool alreadyPresent = retained.Any(m =>
            m.Role == "assistant" &&
            m.ToolCalls?.Any(tc =>
                string.Equals(tc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase) &&
                tc.ArgsSummary?.EndsWith(routeKeyword, StringComparison.OrdinalIgnoreCase) == true) == true);
        if (alreadyPresent) return;

        // Inject a synthetic user message with the signal on its own line.
        // Placed after the compaction summary (index 1) so it appears as early context.
        var synthetic = new AgentMessage
        {
            AgentName = lastHandoff.AgentName,
            Content   = $"[Resume: pre-compaction routing signal from {lastHandoff.AgentName}]\n{routeKeyword}",
            Role      = "user",
            TurnIndex = lastHandoff.TurnIndex,
        };

        int insertAt = retained.Count > 0 && retained[0].IsCompactionSummary ? 1 : 0;
        retained.Insert(insertAt, synthetic);
    }

    // Resets all per-compaction-cycle state in one place. Every counter or flag that
    // must restart after a compaction belongs here — adding it anywhere else means the
    // next person to introduce a new counter will miss this site.
    // Note: _totalAssistantTurnCount is session-lifetime (MaxIterations cap) and intentionally
    // does not appear here.
    private void PostCompactionReset(SessionCheckpoint _)
    {
        _perAgentCumulativeInputTokens.Clear();
        _warnedAgents.Clear();
        _justCompacted = true;
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
            // Checkpoint save failed (e.g. disk full, permissions). Non-fatal: session continues
            // in memory. The next successful save will catch up.
            if (statusActive) AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[yellow]  ⚠ Checkpoint save failed: {Markup.Escape(TrimTo(saveEx.Message, 200))}[/]");
        }

        // Accumulate per-agent cumulative input tokens unconditionally — needed for
        // budget enforcement and context window recording regardless of grace state.
        var agentName  = msg.AgentName ?? "Unknown";
        int inputToks  = 0;
        int cumulative = 0;
        if (msg.Usage?.InputTokens is > 0 and var rawInputToks)
        {
            inputToks = rawInputToks;
            _perAgentCumulativeInputTokens[agentName] =
                _perAgentCumulativeInputTokens.GetValueOrDefault(agentName) + inputToks;
            cumulative = _perAgentCumulativeInputTokens[agentName];

            if (contextWindowRecorder is not null)
                await contextWindowRecorder.RecordAsync(
                    agentName:             agentName,
                    turn:                  msg.TurnIndex,
                    turnInputTokens:       inputToks,
                    turnOutputTokens:      msg.Usage.OutputTokens,
                    cumulativeInputTokens: cumulative,
                    warnAt:                contextBudget?.WarnAt,
                    cutoverAt:             contextBudget?.CutoverAt);

            if (contextBudget?.WarnAt > 0 && cumulative >= contextBudget.WarnAt
                && _warnedAgents.Add(agentName))
            {
                if (statusActive) AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(
                    $"[yellow]  ⚠ {Markup.Escape(agentName)} has accumulated {cumulative:N0} cumulative " +
                    $"input tokens (warn_at: {contextBudget.WarnAt:N0}). " +
                    $"Context rot risk — compaction will trigger at {contextBudget.CutoverAt:N0} tokens.[/]");
                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("context_budget_warn",
                        agent: agentName,
                        payload: new { cumulative_input_tokens = cumulative, warn_at = contextBudget.WarnAt, cutover_at = contextBudget.CutoverAt });
            }
        }

        // Post-compaction grace: skip cumulative-budget compaction triggers for exactly one turn
        // to avoid thrashing. Token accumulation above still runs so budget recording stays accurate.
        // MaxSingleTurnInputTokens is checked first and is NOT suppressed — a single-turn explosion
        // must always trigger compaction even on the turn immediately after compaction.
        if (contextBudget is not null && inputToks > 0 &&
            contextBudget.MaxSingleTurnInputTokens > 0 && inputToks > contextBudget.MaxSingleTurnInputTokens)
        {
            _justCompacted = false;
            _pendingCompactionReason = CompactionReason.SingleTurnLimit;
            if (statusActive) AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[yellow]  ⚡ {Markup.Escape(agentName)} single-turn input ({inputToks:N0}) exceeded " +
                $"MaxSingleTurnInputTokens ({contextBudget.MaxSingleTurnInputTokens:N0}). " +
                $"Compacting before next turn...[/]");
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync("context_budget_cutover",
                    agent: agentName,
                    payload: new { input_tokens = inputToks, cutover_at = contextBudget.MaxSingleTurnInputTokens, reason = CompactionReason.SingleTurnLimit });
            return true;
        }

        if (_justCompacted)
        {
            _justCompacted = false;
            return false;
        }

        if (compactor?.ShouldCompact(checkpoint.Messages) == true)
        {
            _pendingCompactionReason = CompactionReason.ShouldCompact;
            return true;
        }

        if (compactor is not null &&
            msg.ToolCalls?.Any(tc => tc.Name == CompactionPlugin.FunctionName) == true)
        {
            _pendingCompactionReason = CompactionReason.AgentRequested;
            return true;
        }

        if (contextBudget is not null && inputToks > 0)
        {
            if (contextBudget.CutoverAt > 0 && cumulative >= contextBudget.CutoverAt)
            {
                _pendingCompactionReason = CompactionReason.CumulativeBudget;
                AnsiConsole.MarkupLine(
                    $"[yellow]  ⚡ {Markup.Escape(agentName)} reached context budget cutover " +
                    $"({cumulative:N0} ≥ {contextBudget.CutoverAt:N0} input tokens). Compacting history...[/]");
                if (eventEmitter is not null)
                    await eventEmitter.EmitAsync("context_budget_cutover",
                        agent: agentName,
                        payload: new { cumulative_input_tokens = cumulative, cutover_at = contextBudget.CutoverAt });
                return true;
            }
        }

        return false;
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

    private static string BuildModifiedFilesNote(List<AgentMessage> messages)
    {
        var files = new List<string>();
        foreach (var msg in messages)
        {
            if (msg.ToolCalls is null) continue;
            foreach (var tc in msg.ToolCalls)
            {
                if (!tc.Succeeded) continue;
                if (tc.Name == "write_file" &&
                    tc.ArgsSummary is { } pa &&
                    pa.StartsWith("path=", StringComparison.Ordinal))
                {
                    files.Add(pa["path=".Length..]);
                }
                else if (tc.Name is "shell_run" or "shell_run_script" &&
                         tc.ArgsSummary is { } ca &&
                         ca.StartsWith("command=", StringComparison.Ordinal) &&
                         ca.Contains("sed -i", StringComparison.Ordinal))
                {
                    files.Add($"(sed edit) {ca["command=".Length..]}");
                }
            }
        }
        return files.Count > 0
            ? "\n\nFILES MODIFIED IN THIS SESSION (before compaction):\n" +
              string.Join("\n", files.Distinct().Select(f => $"  - {f}")) +
              "\n\nThese changes are already on disk. Use shell_run('git diff') or shell_run('git status') to verify current state."
            : string.Empty;
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
            // Check type name without taking a hard dependency on System.ClientModel.
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
