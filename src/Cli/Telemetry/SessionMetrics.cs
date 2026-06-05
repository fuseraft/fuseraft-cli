using Spectre.Console;
using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Telemetry;

/// <summary>
/// Accumulates per-session quality metrics and renders a summary at session end.
/// Populated by <see cref="SessionRunner"/> via <see cref="RecordTurn"/>,
/// <see cref="RecordCompaction"/>, and <see cref="RecordCacheHit"/>.
/// </summary>
public sealed class SessionMetrics
{
    private int  _totalTurns;
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private int  _maxTurnInputTokens;
    private int  _totalToolCalls;
    private int  _totalPatchFailures;
    private int  _totalDuplicateReads;
    private int  _totalCompactions;
    private string? _lastCompactionReason;

    // Per-turn running list for the turn_metrics event.
    private readonly List<TurnSnapshot> _turns = [];

    /// <summary>
    /// Called by <see cref="SessionRunner"/> for every yielded <see cref="AgentMessage"/>.
    /// Non-assistant messages are ignored.
    /// </summary>
    public void RecordTurn(AgentMessage msg)
    {
        if (msg.Role != "assistant") return;

        _totalTurns++;
        var input  = msg.Usage?.InputTokens  ?? 0;
        var output = msg.Usage?.OutputTokens ?? 0;
        _totalInputTokens  += input;
        _totalOutputTokens += output;
        if (input > _maxTurnInputTokens) _maxTurnInputTokens = input;

        var tools         = msg.ToolCalls?.Count ?? 0;
        var patchFailures = msg.ToolCalls?.Count(tc =>
            tc.Name == "patch_file" && !tc.Succeeded) ?? 0;

        _totalToolCalls    += tools;
        _totalPatchFailures += patchFailures;

        _turns.Add(new TurnSnapshot(
            msg.TurnIndex,
            msg.AgentName,
            input,
            output,
            tools,
            patchFailures));
    }

    /// <summary>Increment the duplicate-read counter. Wired to <see cref="Infrastructure.Plugins.FileSystemPlugin"/> via callback.</summary>
    public void RecordCacheHit() => Interlocked.Increment(ref _totalDuplicateReads);

    /// <summary>Record that a compaction cycle ran and the reason it was triggered.</summary>
    public void RecordCompaction(string reason = "budget")
    {
        _totalCompactions++;
        _lastCompactionReason = reason;
    }

    /// <summary>
    /// Prints the session summary table to the console and emits a <c>session_summary</c>
    /// event via <paramref name="eventEmitter"/> when non-null.
    /// </summary>
    public async Task PrintSummaryAsync(EventEmitter? eventEmitter, string sessionId)
    {
        if (_totalTurns == 0) return;

        var avgInput = _totalTurns > 0 ? _totalInputTokens / _totalTurns : 0;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Session Summary[/]");

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Metric");
        table.AddColumn(new TableColumn("Value").RightAligned());

        table.AddRow("Turns",              _totalTurns.ToString("N0"));
        table.AddRow("Max turn tokens",    _maxTurnInputTokens.ToString("N0"));
        table.AddRow("Avg turn tokens",    avgInput.ToString("N0"));
        table.AddRow("Total tool calls",   _totalToolCalls.ToString("N0"));
        table.AddRow("Duplicate reads",    _totalDuplicateReads.ToString("N0"));
        table.AddRow("Patch failures",     _totalPatchFailures.ToString("N0"));
        table.AddRow("Compactions",        _totalCompactions.ToString("N0"));

        AnsiConsole.Write(table);

        if (eventEmitter is not null)
            await eventEmitter.EmitAsync("session_summary",
                payload: new
                {
                    total_turns             = _totalTurns,
                    max_turn_input_tokens   = _maxTurnInputTokens,
                    avg_turn_input_tokens   = avgInput,
                    total_input_tokens      = _totalInputTokens,
                    total_output_tokens     = _totalOutputTokens,
                    total_tool_calls        = _totalToolCalls,
                    duplicate_reads         = _totalDuplicateReads,
                    patch_failures          = _totalPatchFailures,
                    compactions             = _totalCompactions,
                    last_compaction_reason  = _lastCompactionReason,
                });
    }

    private sealed record TurnSnapshot(
        int    TurnIndex,
        string AgentName,
        int    InputTokens,
        int    OutputTokens,
        int    ToolCalls,
        int    PatchFailures);
}
