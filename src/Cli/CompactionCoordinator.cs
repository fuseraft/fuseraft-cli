using System.Diagnostics;
using Spectre.Console;
using fuseraft.Cli.Telemetry;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;
using fuseraft.Orchestration.Strategies;
using MagenticOrchestrator = fuseraft.Orchestration.MagenticOrchestrator;

namespace fuseraft.Cli;

/// <summary>
/// Owns the compaction state machine: the pending compaction reason, the post-compaction
/// grace flag, and all compaction execution logic. Extracted from <c>SessionRunner</c> so
/// those concerns don't accumulate further on that class.
/// </summary>
internal sealed class CompactionCoordinator(
    IOrchestrator orchestrator,
    ConversationCompactor? compactor,
    ISessionStore sessionStore,
    EventEmitter? eventEmitter,
    SessionMetrics? sessionMetrics,
    ContextWindowRecorder? contextWindowRecorder,
    Func<string, string> resumeHint)
{
    // Reason for the pending compaction cycle — set just before compactionNeeded=true,
    // read inside ApplyCompactionAsync for the compaction event payload.
    private string _pendingCompactionReason = CompactionReason.ShouldCompact;

    // Set to true after each compaction cycle. Suppresses CutoverAt (cumulative) enforcement
    // for exactly one turn so a post-compaction turn can run without immediately re-compacting.
    // MaxSingleTurnInputTokens is NOT suppressed: a single-turn explosion must always compact.
    private bool _justCompacted;

    public void SetPendingReason(string reason) => _pendingCompactionReason = reason;

    // Returns true when the pre-turn context-size estimate already exceeds MaxSingleTurnInputTokens.
    // Skipped when _justCompacted is true to avoid thrashing after a compaction that left a large tail.
    public bool NeedsPreTurnCompaction(SessionCheckpoint checkpoint, ContextBudgetConfig? contextBudget) =>
        !_justCompacted
        && compactor is not null
        && contextBudget?.MaxSingleTurnInputTokens > 0
        && checkpoint.Messages.Sum(m => (m.Content?.Length ?? 0) / 3) > contextBudget.MaxSingleTurnInputTokens;

    // Applies the compaction trigger policy in order and returns true when compaction is needed.
    // Fires UI messages and events for the triggers that are actually honored.
    public async Task<bool> EvaluateCompactionTriggerAsync(
        SessionCheckpoint checkpoint,
        AgentMessage msg,
        BudgetEvalResult budgetResult,
        bool statusActive)
    {
        var agentName = msg.AgentName ?? AgentNames.Unknown;

        // SingleTurnLimit: never suppressed by _justCompacted — a per-turn explosion must
        // always compact even on the turn immediately after a previous compaction.
        if (budgetResult.SingleTurnTrigger)
        {
            _justCompacted = false;
            _pendingCompactionReason = CompactionReason.SingleTurnLimit;
            if (statusActive) AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[yellow]  ⚡ {Markup.Escape(agentName)} single-turn input ({budgetResult.InputTokens:N0}) exceeded " +
                $"MaxSingleTurnInputTokens ({budgetResult.SingleTurnThreshold:N0}). " +
                $"Compacting before next turn...[/]");
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.ContextBudgetCutover,
                    agent: agentName,
                    payload: new { input_tokens = budgetResult.InputTokens, cutover_at = budgetResult.SingleTurnThreshold, reason = CompactionReason.SingleTurnLimit });
            return true;
        }

        // Post-compaction grace: skip cumulative-budget and window-size triggers for one turn.
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

        if (budgetResult.CutoverTrigger)
        {
            _pendingCompactionReason = CompactionReason.CumulativeBudget;
            AnsiConsole.MarkupLine(
                $"[yellow]  ⚡ {Markup.Escape(agentName)} reached context budget cutover " +
                $"({budgetResult.CumulativeInputTokens:N0} ≥ {budgetResult.CutoverThreshold:N0} tokens).[/]");
            AnsiConsole.MarkupLine($"[yellow]  Compacting history...[/]");
            if (eventEmitter is not null)
                await eventEmitter.EmitAsync(EventTypes.ContextBudgetCutover,
                    agent: agentName,
                    payload: new { cumulative_input_tokens = budgetResult.CumulativeInputTokens, cutover_at = budgetResult.CutoverThreshold });
            return true;
        }

        return false;
    }

    // Resets per-compaction-cycle state. Called after every successful compaction.
    // _totalAssistantTurnCount is session-lifetime and intentionally excluded.
    public void PostCompactionReset(ContextBudgetManager budgetManager)
    {
        budgetManager.Reset();
        _justCompacted = true;
    }

    public async Task<(SessionCheckpoint Checkpoint, bool ShouldBreak, bool ShouldContinue, string? ErrorMessage)>
        TryTriggerCompactionAsync(
            string task,
            SessionCheckpoint checkpoint,
            int totalAssistantTurnCount,
            ContextBudgetManager budgetManager,
            CancellationToken cancellationToken)
    {
        try
        {
            checkpoint = await ApplyCompactionAsync(task, checkpoint, compactor!, cancellationToken);
            PostCompactionReset(budgetManager);
            if (contextWindowRecorder is not null)
                await contextWindowRecorder.RecordCompactionAsync(totalAssistantTurnCount);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine(
                $"\n[yellow]Session [bold]{checkpoint.SessionId}[/] paused — resume with:[/] " +
                $"[dim]{Markup.Escape(resumeHint(checkpoint.SessionId))}[/]");
            return (checkpoint, ShouldBreak: true, ShouldContinue: false, ErrorMessage: "Cancelled.");
        }
        catch (Exception ex)
        {
            string? dumpPath = null;
            try { dumpPath = CrashDumper.Write(ex, []); } catch { }
            AnsiConsole.MarkupLine(
                $"\n[red]✗ Compaction error:[/] {Markup.Escape(TrimTo(ex.Message, 300))}");
            if (dumpPath is not null)
                AnsiConsole.MarkupLine($"  [dim]Crash dump: {Markup.Escape(dumpPath)}[/]");
            AnsiConsole.MarkupLine(
                $"\n[yellow]Session [bold]{checkpoint.SessionId}[/] saved — resume with:[/] " +
                $"[dim]{Markup.Escape(resumeHint(checkpoint.SessionId))}[/]");
            return (checkpoint, ShouldBreak: true, ShouldContinue: false, ErrorMessage: $"Compaction failed: {ex.Message}");
        }

        if (checkpoint.ResumeExecutorId is not null)
            orchestrator.SetResumeExecutorId(checkpoint.ResumeExecutorId);
        if (checkpoint.CurrentStateName is not null)
            orchestrator.SetResumeStateName(checkpoint.CurrentStateName);

        if (orchestrator is AgentOrchestrator ao && checkpoint.StateMachineState is { } smState)
            ao.SetResumeSnapshot(smState);

        if (orchestrator is MagenticOrchestrator magentic && checkpoint.MagenticState is { } magState)
            magentic.SetResumeState(magState);

        AnsiConsole.MarkupLine("[dim]History compacted — continuing session.[/]");
        return (checkpoint, ShouldBreak: false, ShouldContinue: true, ErrorMessage: null);
    }

    private async Task<SessionCheckpoint> ApplyCompactionAsync(
        string task,
        SessionCheckpoint checkpoint,
        ConversationCompactor compactor,
        CancellationToken cancellationToken)
    {
        // Capture which executor is active before discarding full history so the next
        // StreamAsync starts from the correct agent. Skip for Magentic: the last assistant
        // message there is often a manager tag like "[MagenticManager:Final]" which would
        // write a misleading executor ID into the checkpoint.
        string? lastAssistantAgent = null;
        if (orchestrator is not MagenticOrchestrator)
        {
            lastAssistantAgent = checkpoint.Messages
                .LastOrDefault(m => m.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(m.AgentName))
                ?.AgentName
                ?.ToLowerInvariant();

            checkpoint.ResumeExecutorId = lastAssistantAgent;
        }

        string modifiedFilesNote = BuildModifiedFilesNote(checkpoint.Messages);

        var snapshotter = (orchestrator as AgentOrchestrator)?.CurrentSnapshotter;

        if (snapshotter is not null)
        {
            try
            {
                var snap = await snapshotter.SnapshotAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(snap.CurrentStateName))
                    checkpoint.CurrentStateName = snap.CurrentStateName;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Debug.WriteLine($"[CompactionCoordinator] state snapshot failed: {ex.Message}"); }
        }

        if (snapshotter is StateMachineSelectionStrategy smStrategy)
        {
            try { checkpoint.StateMachineState = smStrategy.TakeCheckpointState(); }
            catch (Exception ex) { Debug.WriteLine($"[CompactionCoordinator] failure-state capture failed: {ex.Message}"); }
        }

        if (orchestrator is not MagenticOrchestrator && eventEmitter is not null)
            _ = eventEmitter.EmitAsync(EventTypes.CompactionResumeCandidate,
                payload: new
                {
                    last_assistant_agent = lastAssistantAgent,
                    current_state_name   = checkpoint.CurrentStateName,
                    reason               = _pendingCompactionReason,
                    total_messages       = checkpoint.Messages.Count,
                });

        int turnsBefore = checkpoint.Messages.Count;

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
                await eventEmitter.EmitAsync(EventTypes.Compaction,
                    payload: new
                    {
                        mode           = CompactionModes.Window,
                        reason         = _pendingCompactionReason,
                        turns_dropped  = dropped,
                        turns_retained = trimmed.Count,
                        resume_from    = checkpoint.ResumeExecutorId ?? "planner"
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
            await eventEmitter.EmitAsync(EventTypes.Compaction,
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
    private static void TryPinLastRoutingSignal(
        List<AgentMessage> retained,
        IReadOnlyList<AgentMessage> original)
    {
        AgentMessage? lastHandoff = null;
        for (int i = original.Count - 1; i >= 0; i--)
        {
            var m = original[i];
            if (m.Role == MessageRole.Assistant &&
                m.ToolCalls?.Any(tc => string.Equals(tc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase)) == true)
            {
                lastHandoff = m;
                break;
            }
        }
        if (lastHandoff is null) return;

        var handoffCall = lastHandoff.ToolCalls!.First(tc =>
            string.Equals(tc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase));
        var argsSummary = handoffCall.ArgsSummary;
        if (argsSummary is null) return;

        var prefix = $"{HandoffPlugin.ArgumentName}=";
        var routeKeyword = argsSummary.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? argsSummary[prefix.Length..].Trim()
            : null;
        if (string.IsNullOrEmpty(routeKeyword)) return;

        bool alreadyPresent = retained.Any(m =>
            m.Role == MessageRole.Assistant &&
            m.ToolCalls?.Any(tc =>
                string.Equals(tc.Name, HandoffPlugin.FunctionName, StringComparison.OrdinalIgnoreCase) &&
                tc.ArgsSummary?.EndsWith(routeKeyword, StringComparison.OrdinalIgnoreCase) == true) == true);
        if (alreadyPresent) return;

        // Appended at the end so TransitionAlreadyFired finds no [fuseraft:] markers after it —
        // inserting at the front would place it before retained transition markers and incorrectly
        // suppress the signal.
        var synthetic = new AgentMessage
        {
            AgentName = lastHandoff.AgentName,
            Content   = $"[Resume: pre-compaction routing signal from {lastHandoff.AgentName}]\n{routeKeyword}",
            Role      = "user",
            TurnIndex = lastHandoff.TurnIndex,
        };

        retained.Add(synthetic);
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

    private static string TrimTo(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
