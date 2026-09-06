using Microsoft.Extensions.AI;
using Spectre.Console;
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
            else
                AnsiConsole.MarkupLine($"[red]✗ Compaction failed:[/] {Markup.Escape(errorReason ?? "unknown error")}");
            return CommandResult.Continue;
        }

        // /compact resets the displayed turn counter so status lines restart from 1.
        ctx.TurnIndex = 0;

        if (ctx.JsonMode)
            ReplJsonBridge.Emit(new { type = "compacted" });
        else
            AnsiConsole.MarkupLine("[dim]Session compacted — history replaced with handoff summary.[/]");
        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/compact", arg });
        return CommandResult.Continue;
    }

    /// <summary>
    /// Core compaction logic shared by the /compact command and the compact_context tool.
    /// Generates a handoff summary via LLM, replaces ctx.History, and resets per-turn
    /// metrics. Returns (success, errorReason, tokensBefore, tokensAfter).
    /// </summary>
    internal static async Task<(bool Success, string? ErrorReason, int BeforeEst, int AfterEst)>
        CompactHistoryAsync(
            ReplSessionContext ctx, string? focus, CancellationToken cancellationToken,
            string source = "manual")
    {
        var beforeEst = ctx.EstimateTokens();
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

        var messages = new List<ChatMessage>(ctx.History) { new ChatMessage(ChatRole.User, compactionPrompt) };

        string summary;
        try
        {
            var mc       = ctx.Factory.Create(ctx.ModelConfig);
            using var _  = mc as IDisposable;
            var response = await mc.GetResponseAsync(messages, cancellationToken: cancellationToken);
            summary      = response.Text ?? string.Empty;
        }
        catch (OperationCanceledException) { return (false, "cancelled", 0, 0); }
        catch (Exception ex)               { return (false, ex.Message,   0, 0); }

        if (string.IsNullOrWhiteSpace(summary)) return (false, "empty", 0, 0);

        var sys = ctx.History.FirstOrDefault(m => m.Role == ChatRole.System);
        ctx.History.Clear();
        if (sys is not null) ctx.History.Add(sys);
        ctx.History.Add(new ChatMessage(ChatRole.User, $"[Compacted context from previous session]\n\n{summary}"));

        ctx.PrevTurnTokenEstimate = 0;
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
