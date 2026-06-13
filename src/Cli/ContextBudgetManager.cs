using Spectre.Console;
using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace fuseraft.Cli;

internal readonly record struct BudgetEvalResult(
    int InputTokens,
    int CumulativeInputTokens,
    bool SingleTurnTrigger,
    int  SingleTurnThreshold,
    bool CutoverTrigger,
    int  CutoverThreshold);

/// <summary>
/// Tracks per-agent cumulative input tokens, fires WarnAt warnings, records context-window
/// snapshots, and signals SingleTurnLimit / CutoverAt compaction thresholds to the caller.
/// Does not own the compaction decision — that belongs to <see cref="CompactionCoordinator"/>.
/// </summary>
internal sealed class ContextBudgetManager(
    ContextBudgetConfig? contextBudget,
    ContextWindowRecorder? contextWindowRecorder,
    EventEmitter? eventEmitter)
{
    private readonly Dictionary<string, int> _perAgentCumulativeInputTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedAgents = new(StringComparer.OrdinalIgnoreCase);

    // Called by CompactionCoordinator.PostCompactionReset after each successful compaction.
    public void Reset()
    {
        _perAgentCumulativeInputTokens.Clear();
        _warnedAgents.Clear();
    }

    /// <summary>
    /// Accumulates token counts, records context-window snapshots, emits WarnAt warnings,
    /// and returns a <see cref="BudgetEvalResult"/> indicating whether a compaction threshold
    /// was crossed. The caller is responsible for honoring any trigger (applying suppression
    /// guards such as <c>_justCompacted</c> is the coordinator's job).
    /// </summary>
    public async Task<BudgetEvalResult> EvaluateAsync(AgentMessage msg, bool statusActive)
    {
        var agentName  = msg.AgentName ?? AgentNames.Unknown;
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
                    await eventEmitter.EmitAsync(EventTypes.ContextBudgetWarn,
                        agent: agentName,
                        payload: new { cumulative_input_tokens = cumulative, warn_at = contextBudget.WarnAt, cutover_at = contextBudget.CutoverAt });
            }
        }

        bool singleTurnTrigger =
            contextBudget is not null && inputToks > 0
            && contextBudget.MaxSingleTurnInputTokens > 0
            && inputToks > contextBudget.MaxSingleTurnInputTokens;

        // CutoverAt is mutually exclusive with SingleTurnLimit: if both thresholds fire on the
        // same turn, SingleTurnLimit takes precedence (it is also not suppressible by _justCompacted).
        bool cutoverTrigger =
            !singleTurnTrigger
            && contextBudget is not null && inputToks > 0
            && contextBudget.CutoverAt > 0
            && cumulative >= contextBudget.CutoverAt;

        return new BudgetEvalResult(
            InputTokens:          inputToks,
            CumulativeInputTokens: cumulative,
            SingleTurnTrigger:    singleTurnTrigger,
            SingleTurnThreshold:  contextBudget?.MaxSingleTurnInputTokens ?? 0,
            CutoverTrigger:       cutoverTrigger,
            CutoverThreshold:     contextBudget?.CutoverAt ?? 0);
    }
}
