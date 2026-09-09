using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Core;
using fuseraft.Core.Models;

namespace fuseraft.Cli.Commands.Repl;

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /plan
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdPlanAsync(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            if (ctx.CurrentPlan is null)
            {
                AnsiConsole.MarkupLine("[dim]No plan. Use[/] [bold]/plan <task>[/] [dim]to create one.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]Current plan ({ctx.CurrentPlan.Length} steps):[/]");
                AnsiConsole.WriteLine();
                foreach (var ps in ctx.CurrentPlan)
                {
                    AnsiConsole.MarkupLine($"  [bold]{ps.Step}.[/] {Markup.Escape(ps.Description)}");
                    if (ps.Tool    is not null) AnsiConsole.MarkupLine($"       [dim]tool: {Markup.Escape(ps.Tool)}[/]");
                    if (ps.Creates is not null) AnsiConsole.MarkupLine($"       [dim]creates: {Markup.Escape(ps.Creates)}[/]");
                }
            }
            return CommandResult.Continue;
        }

        var planPrompt =
            $"Think through the following task and output a plan as a JSON array only. " +
            $"No prose before or after — output ONLY valid JSON starting with '[' and ending with ']'. " +
            $"Each element MUST have: \"step\" (integer), \"description\" (string, the action to take), " +
            $"and \"tool\" (string, the exact name of the tool you will call for this step — e.g. " +
            $"list_files, read_file, patch_file, shell_run, git_add, git_commit). " +
            $"Optionally include \"creates\" (path of a file or directory you will create, relative to " +
            $"the working directory). " +
            $"Focus on intentful actions only — no defensive steps like verifying CWD or reading files back." +
            $"\n\nTask: {arg}";

        ctx.CurrentPlanRequest = arg;
        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/plan", task = arg });
        return CommandResult.Send(planPrompt, capturePlan: true);
    }

    // -------------------------------------------------------------------------
    // /execute
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdExecuteAsync(ReplSessionContext ctx)
    {
        if (ctx.CurrentPlan is null)
        {
            AnsiConsole.MarkupLine("[dim]No plan to execute. Use[/] [bold]/plan <task>[/] [dim]to create one first.[/]");
            return CommandResult.Continue;
        }

        ctx.ExecutionQueue.Clear();
        var ordered = TopologicalSort(ctx.CurrentPlan);
        var total   = ordered.Length;
        foreach (var ps in ordered)
            ctx.ExecutionQueue.Enqueue((ps, total));
        ctx.CurrentPlan = null;

        AnsiConsole.MarkupLine($"[dim]Executing {total}-step plan…[/]");
        AnsiConsole.WriteLine();
        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/execute", steps = total });
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /resume
    // -------------------------------------------------------------------------

    private static CommandResult CmdResume(ReplSessionContext ctx)
    {
        if (ctx.HaltedAt is null)
        {
            AnsiConsole.MarkupLine("[dim]No halted plan to resume.[/]");
            return CommandResult.Continue;
        }
        var (step, total) = ctx.HaltedAt.Value;
        ctx.ExecutionQueue.Enqueue((step, total));
        while (ctx.HaltedRemaining.Count > 0) ctx.ExecutionQueue.Enqueue(ctx.HaltedRemaining.Dequeue());
        ctx.HaltedAt = null;
        ctx.HaltedToolCalls.Clear();
        AnsiConsole.MarkupLine($"[dim]Resuming from step {step.Step} of {total}…[/]");
        AnsiConsole.WriteLine();
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /recover
    // -------------------------------------------------------------------------

    private static CommandResult CmdRecover(ReplSessionContext ctx)
    {
        if (ctx.HaltedAt is null)
        {
            AnsiConsole.MarkupLine("[dim]No halted plan to recover.[/]");
            return CommandResult.Continue;
        }
        var (step, total) = ctx.HaltedAt.Value;
        var toolsCalledStr = ctx.HaltedToolCalls.Count > 0
            ? string.Join(", ", ctx.HaltedToolCalls)
            : "none";

        AnsiConsole.MarkupLine($"[dim]  Halted step:[/] {step.Step} of {total} — {Markup.Escape(step.Description)}");
        if (step.Tool is not null)
        {
            AnsiConsole.MarkupLine($"[dim]  Expected tool:[/] {Markup.Escape(step.Tool)}");
            AnsiConsole.MarkupLine($"[dim]  Tools called:[/]  {Markup.Escape(toolsCalledStr)}");
        }
        AnsiConsole.WriteLine();

        ctx.RecoveryHint =
            $"[Recovery] Step {step.Step} of {total} previously failed: {step.Description}." +
            (step.Tool is not null
                ? $" Expected tool: {step.Tool}. Tools actually called: {toolsCalledStr}."
                : string.Empty) +
            " Diagnose the issue before retrying.";

        ctx.ExecutionQueue.Enqueue((step, total));
        while (ctx.HaltedRemaining.Count > 0) ctx.ExecutionQueue.Enqueue(ctx.HaltedRemaining.Dequeue());
        ctx.HaltedAt = null;
        ctx.HaltedToolCalls.Clear();
        AnsiConsole.MarkupLine($"[dim]Recovery context set. Retrying from step {step.Step}…[/]");
        AnsiConsole.WriteLine();
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /compact
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdCompactAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        var nonSystem = ctx.History.Where(m => m.Role != ChatRole.System).ToList();
        if (nonSystem.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Nothing to compact — no conversation turns yet.[/]");
            return CommandResult.Continue;
        }

        if (!ctx.JsonMode) AnsiConsole.Markup("[dim]compacting…[/]");

        var (success, errorReason, _, _) = await CompactHistoryAsync(ctx, arg, cancellationToken);

        if (!ctx.JsonMode) Console.Write($"\r{new string(' ', 30)}\r");

        if (!success)
        {
            if (errorReason == "cancelled")
                AnsiConsole.MarkupLine("[dim](cancelled)[/]");
            else if (errorReason == "empty")
                AnsiConsole.MarkupLine("[yellow]Compaction returned empty output — history unchanged.[/]");
            else if (errorReason == "nothing_to_compact")
                AnsiConsole.MarkupLine("[dim]Nothing to compact — recent history already fits within the preserved window.[/]");
            else if (errorReason == "not_smaller")
                AnsiConsole.MarkupLine("[yellow]Compaction didn't shrink the history — keeping it as-is rather than risking lost context for no gain.[/]");
            else
                AnsiConsole.MarkupLine($"[red]✗ Compaction failed:[/] {Markup.Escape(errorReason ?? "unknown error")}");
            return CommandResult.Continue;
        }

        // /compact resets the displayed turn counter so status lines restart from 1.
        ctx.TurnIndex = 0;
        ctx.LastExtractedTurnIndex = -1;

        if (ctx.JsonMode)
            ReplJsonBridge.Emit(new { type = "compacted" });
        else
            AnsiConsole.MarkupLine("[dim]Session compacted — history replaced with handoff summary.[/]");
        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/compact", arg });
        return CommandResult.Continue;
    }

    // Fraction of ctx.ContextTokenBudget kept verbatim as the most recent whole turn-groups
    // (see FindPreserveTailStart) rather than folded into the LLM summary. Mirrors Cline's
    // preserveRecentTokens (fixed 20k / 128k default budget ≈ 15.6%) scaled to fuseraft's
    // per-model budget instead of a fixed token count. Exists specifically so a session that
    // just made several tool calls doesn't have that work paraphrased away right when it's
    // most likely to still matter — only what's older than the tail gets summarized.
    private const double PreserveRecentTailRatio = 0.20;

    private static int EstimateMessageListTokens(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => TokenEstimator.EstimateTokens(m.Contents.Sum(AgentContextCompactionFilters.EstimateContentChars)));

    // Returns the index in `history` where the verbatim "preserve recent" tail should begin —
    // the start of a User-led turn group (mirrors ReplTurn.TrimHistory's grouping: a User
    // message plus every following non-User message, so a tool call's FunctionCallContent is
    // never separated from its FunctionResultContent, which providers reject). Walks backward
    // from the end, accumulating whole groups until at least `preserveTokens` are covered —
    // always keeps at least the single most recent group, even when preserveTokens is 0.
    private static int FindPreserveTailStart(List<ChatMessage> history, int sysEnd, int preserveTokens)
    {
        var groupStarts = new List<int>();
        for (int i = sysEnd; i < history.Count; i++)
            if (history[i].Role == ChatRole.User) groupStarts.Add(i);

        if (groupStarts.Count == 0) return history.Count;

        int total = 0;
        int keepFromGroup = groupStarts.Count;
        for (int g = groupStarts.Count - 1; g >= 0; g--)
        {
            int groupEnd = g + 1 < groupStarts.Count ? groupStarts[g + 1] : history.Count;
            for (int i = groupStarts[g]; i < groupEnd; i++)
                total += TokenEstimator.EstimateTokens(history[i].Contents.Sum(AgentContextCompactionFilters.EstimateContentChars));
            keepFromGroup = g;
            if (total >= preserveTokens) break;
        }
        return groupStarts[keepFromGroup];
    }

    /// <summary>
    /// Core compaction logic shared by the /compact command and the compact_context tool.
    /// Summarises everything older than a verbatim recent tail via LLM, replaces ctx.History
    /// with [system?, summary, ...preserved tail], and resets per-turn metrics. Returns
    /// (success, errorReason, tokensBefore, tokensAfter).
    /// </summary>
    internal static async Task<(bool Success, string? ErrorReason, int BeforeEst, int AfterEst)>
        CompactHistoryAsync(
            ReplSessionContext ctx, string? focus, CancellationToken cancellationToken,
            string source = "manual")
    {
        var beforeEst = ctx.EstimateTokens();

        var sys    = ctx.History.FirstOrDefault(m => m.Role == ChatRole.System);
        int sysEnd = ctx.History.Count > 0 && ctx.History[0].Role == ChatRole.System ? 1 : 0;

        var preserveTokens = (int)(ctx.ContextTokenBudget * PreserveRecentTailRatio);
        var tailStart       = FindPreserveTailStart(ctx.History, sysEnd, preserveTokens);
        var toSummarize     = ctx.History.Skip(sysEnd).Take(tailStart - sysEnd).ToList();
        var preservedTail   = ctx.History.Skip(tailStart).ToList();

        // Recent history alone already fits the preserved window — nothing old enough to
        // fold into a summary, so don't spend an LLM call summarising nothing.
        if (toSummarize.Count == 0) return (false, "nothing_to_compact", beforeEst, beforeEst);

        var focusNote = string.IsNullOrWhiteSpace(focus) ? string.Empty : $"\n\nFocus for the next session: {focus}";
        var compactionPrompt =
            "Write a concise handoff document summarising this conversation so a fresh session can continue the work. " +
            "Include: what was being worked on, key decisions and findings, current state, and what comes next. " +
            "Reference file paths and symbols by name rather than quoting their full content. " +
            "Redact any sensitive values such as API keys or passwords. " +
            "For any facts about files, code, or system state that the assistant stated WITHOUT a corresponding tool call " +
            "in that same turn (e.g. claimed a file exists, described code contents, or reported a command result without " +
            "calling read_file / shell_run / grep_file etc.), do NOT include them as established facts. " +
            "Instead write: [UNVERIFIED ASSUMPTION: <one-line description>]. " +
            "Facts confirmed by actual tool output are verified and should be stated normally." +
            focusNote;

        var messages = new List<ChatMessage>(toSummarize) { new ChatMessage(ChatRole.User, compactionPrompt) };

        string summary;
        try
        {
            // Route through ctx.Client rather than a bare ctx.Factory.Create client: this call
            // fires exactly when history is largest (75%+ of budget, or a forced post-overflow
            // recovery), so it needs the same AgentMiddlewareBuilder adaptive-trim-and-retry
            // protection normal turns get. No ChatOptions/tools are passed, so the wrapped
            // FunctionInvokingChatClient just runs a single pass-through round — it never invokes
            // a tool mid-summarization.
            var response = await ctx.Client.GetResponseAsync(messages, cancellationToken: cancellationToken);
            summary      = response.Text ?? string.Empty;
        }
        catch (OperationCanceledException) { return (false, "cancelled", 0, 0); }
        catch (Exception ex)               { return (false, ex.Message,   0, 0); }

        if (string.IsNullOrWhiteSpace(summary)) return (false, "empty", 0, 0);

        var candidate = new List<ChatMessage>();
        if (sys is not null) candidate.Add(sys);
        candidate.Add(new ChatMessage(ChatRole.User, $"[Compacted context from previous session]\n\n{summary}"));
        candidate.AddRange(preservedTail);

        // Acceptance bar (mirrors Cline's overflow-recovery contract): a compaction that
        // doesn't actually shrink the history isn't worth the fidelity loss + LLM round-trip
        // it cost — reject it and leave ctx.History untouched rather than silently accepting
        // a "compaction" that made things worse or no better.
        var afterMsgTokens  = EstimateMessageListTokens(candidate);
        var beforeMsgTokens = EstimateMessageListTokens(ctx.History);
        if (afterMsgTokens >= beforeMsgTokens)
            return (false, "not_smaller", beforeEst, afterMsgTokens);

        ctx.History.Clear();
        ctx.History.AddRange(candidate);

        ctx.PrevTurnTokenEstimate = 0;
        ctx.PrevCtxEstimate       = 0;
        ctx.TurnTokenDeltas.Clear();
        ctx.ContextWarningShown   = false;
        ctx.ResetPlanState();

        var afterEst = ctx.EstimateTokens();
        await ctx.Emitter.EmitAsync(EventTypes.Compaction, payload: new
        {
            source,
            before_tokens = beforeEst,
            after_tokens  = afterEst,
            focus,
        });
        return (true, null, beforeEst, afterEst);
    }

    // -------------------------------------------------------------------------
    // Topological sort for plan execution order
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <paramref name="steps"/> in dependency order using Kahn's algorithm.
    /// Steps with no <c>DependsOn</c> or with already-satisfied dependencies are emitted
    /// first; within the same dependency tier, steps are ordered by their original step
    /// number. Falls back to the original order if a cycle is detected.
    /// </summary>
    private static PlanStep[] TopologicalSort(PlanStep[] steps)
    {
        if (steps.All(s => s.DependsOn is not { Length: > 0 }))
            return steps;

        // Build index tolerating duplicate step numbers — last writer wins.
        var byId       = new Dictionary<int, PlanStep>();
        var inDegree   = new Dictionary<int, int>();
        var dependents = new Dictionary<int, List<int>>();
        foreach (var s in steps)
        {
            byId[s.Step]       = s;
            inDegree[s.Step]   = 0;
            dependents[s.Step] = new List<int>();
        }

        foreach (var step in steps.Where(s => s.DependsOn is { Length: > 0 }))
        {
            foreach (var dep in step.DependsOn!)
            {
                if (!byId.ContainsKey(dep)) continue;
                inDegree[step.Step]++;
                dependents[dep].Add(step.Step);
            }
        }

        var queue  = new Queue<int>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(id => id));
        var result = new List<PlanStep>(steps.Length);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            result.Add(byId[id]);
            foreach (var dep in dependents[id].OrderBy(x => x))
            {
                if (--inDegree[dep] == 0)
                    queue.Enqueue(dep);
            }
        }

        return result.Count == steps.Length ? [.. result] : steps;
    }
}
