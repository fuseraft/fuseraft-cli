using Spectre.Console;
using fuseraft.Core.Models;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Processes what happens to plan/step state once a turn's response is complete: parses and
/// records a captured plan, and verifies a plan step's structural/shell-command checks.
/// Extracted from <see cref="ReplTurn"/> — narrow <see cref="ReplSessionContext"/> footprint,
/// and <see cref="VerifyStepAsync"/>/<see cref="RunVerifyCommandAsync"/> already take no
/// <c>ctx</c> parameter at all, the same "most self-contained" shape
/// <c>SubGraphExecutor</c> had in the <c>GraphOrchestrator</c> decomposition.
/// </summary>
internal static class ReplTurnOutcome
{
    internal static void HandlePlanCapture(ReplSessionContext ctx, string responseText)
    {
        if (TryParsePlan(responseText, out var steps) && steps.Length > 0)
        {
            ctx.CurrentPlan = steps;
            _ = ctx.Emitter.EmitAsync(EventTypes.PlanCaptured, turn: ctx.TurnIndex, payload: new
            {
                step_count = steps.Length,
                steps = steps.Select(s => new
                {
                    step        = s.Step,
                    description = s.Description,
                    tool        = s.Tool,
                    creates     = s.Creates,
                    verifies    = s.Verifies,
                    depends_on  = s.DependsOn,
                }).ToArray(),
            });
            if (ctx.JsonMode)
            {
                ReplJsonBridge.Emit(new { type = "plan", steps });
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]Plan ({steps.Length} steps). Review, then run[/] [bold]/execute[/][dim].[/]");
                AnsiConsole.WriteLine();
                foreach (var ps in steps)
                {
                    AnsiConsole.MarkupLine($"  [bold]{ps.Step}.[/] {Markup.Escape(ps.Description)}");
                    if (ps.Tool    is not null) AnsiConsole.MarkupLine($"       [dim]tool: {Markup.Escape(ps.Tool)}[/]");
                    if (ps.Creates is not null) AnsiConsole.MarkupLine($"       [dim]creates: {Markup.Escape(ps.Creates)}[/]");
                }
                AnsiConsole.WriteLine();
            }
        }
        else
        {
            if (ctx.JsonMode)
                ReplJsonBridge.Emit(new { type = "error", text = "Could not parse plan JSON from response." });
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠ Could not parse plan JSON. Raw response:[/]");
                Console.WriteLine(responseText);
                AnsiConsole.MarkupLine("[dim]Try /plan again.[/]");
                AnsiConsole.WriteLine();
            }
        }
    }

    internal static async Task<bool> HandleStepResult(
        ReplSessionContext ctx, PlanStep activeStep, int total, List<string> toolCallsThisTurn,
        List<(string ToolName, string Output)> capturedResults, bool hitIterationCap,
        string responseText = "", CancellationToken cancellationToken = default)
    {
        var (passed, verifyOutput) = await VerifyStepAsync(activeStep, toolCallsThisTurn, ctx.Cwd, cancellationToken);
        var stepsLeft = ctx.ExecutionQueue.Count;

        // When deterministic checks pass and adversarial mode is on, ask the critic.
        if (passed && ctx.AdversarialMode && ctx.SubAgent is not null)
        {
            AnsiConsole.Markup("[dim]  critic reviewing…[/]");
            var (approved, reason) = await ctx.SubAgent.CriticReviewAsync(
                activeStep.Description, activeStep.Tool, toolCallsThisTurn, responseText, cancellationToken);
            Console.Write($"\r{new string(' ', 40)}\r");
            if (!approved)
            {
                passed = false;
                ctx.RecoveryHint = $"[Critic] Step {activeStep.Step} rejected: {reason}";
                AnsiConsole.MarkupLine(
                    $"[yellow]  ✗ Critic rejected step {activeStep.Step}: {Markup.Escape(reason ?? "no reason given")}[/]");
            }
        }
        if (passed)
        {
            var zeroCallSkip  = activeStep.Tool is not null && toolCallsThisTurn.Count == 0;
            var inspectSkip   = activeStep.Tool is not null && toolCallsThisTurn.Count > 0 &&
                                toolCallsThisTurn.All(t => InspectTools.Contains(t));
            var skipped       = zeroCallSkip || inspectSkip;
            // Broader than inspectSkip: preserve history whenever only read-only tools were
            // called, even if the step had no expected tool declared.
            ctx.LastStepWasInspectOnly = toolCallsThisTurn.Count > 0 &&
                                        toolCallsThisTurn.All(t => InspectTools.Contains(t));
            ctx.LastStepInspectResults = ctx.LastStepWasInspectOnly && capturedResults.Count > 0
                ? capturedResults : null;
            await ctx.Emitter.EmitAsync(EventTypes.StepComplete, turn: ctx.TurnIndex, payload: new
            {
                step       = activeStep.Step,
                total,
                skipped,
                steps_left = stepsLeft,
                hit_iteration_cap = hitIterationCap,
                verify_output     = verifyOutput,
            });
            if (ctx.JsonMode)
            {
                ReplJsonBridge.Emit(new { type = "step_status", step = activeStep.Step, total, status = skipped ? "skipped" : "complete", stepsLeft });
            }
            else
            {
                var icon  = skipped ? "↷" : "✓";
                var label = skipped ? "skipped" : "complete";
                AnsiConsole.MarkupLine(stepsLeft > 0
                    ? $"[dim]  {icon} Step {activeStep.Step} {label}.  {stepsLeft} step{(stepsLeft == 1 ? "" : "s")} remaining.[/]"
                    : $"[dim]  {icon} Step {activeStep.Step} {label}.  Plan finished.[/]");
                if (hitIterationCap)
                    AnsiConsole.MarkupLine(
                        $"[dim]  ↯ Step {activeStep.Step} reached the {ReplTurn.StepIterationLimit}-round limit; later calls in this step may have been cut short.[/]");
                if (zeroCallSkip && activeStep.Tool is not null && !InspectTools.Contains(activeStep.Tool))
                    AnsiConsole.MarkupLine(
                        $"[yellow]  ⚠ Step {activeStep.Step}: '{Markup.Escape(activeStep.Tool)}' was not called — verify the agent did not fabricate this result.[/]");
            }
        }
        else
        {
            await ctx.Emitter.EmitAsync(EventTypes.StepHalted, turn: ctx.TurnIndex, payload: new
            {
                step              = activeStep.Step,
                total,
                expected_tool     = activeStep.Tool,
                expected_creates  = activeStep.Creates,
                hit_iteration_cap = hitIterationCap,
                tool_calls        = toolCallsThisTurn.ToArray(),
                verify_output     = verifyOutput,
            });
            if (!ctx.JsonMode)
            {
                if (activeStep.Tool is not null &&
                    !toolCallsThisTurn.Any(t => t.Equals(activeStep.Tool, StringComparison.OrdinalIgnoreCase)))
                {
                    if (hitIterationCap)
                        AnsiConsole.MarkupLine(
                            $"[yellow]  ⚠ Step {activeStep.Step}: hit the {ReplTurn.StepIterationLimit}-round limit before " +
                            $"'{Markup.Escape(activeStep.Tool)}' was called — step may be too broad, consider splitting it.[/]");
                    else
                        AnsiConsole.MarkupLine(
                            $"[yellow]  ⚠ Step {activeStep.Step}: expected tool '{Markup.Escape(activeStep.Tool)}' was not called.[/]");
                }
                if (activeStep.Creates is not null &&
                    !File.Exists(Path.Combine(ctx.Cwd, activeStep.Creates)) &&
                    !Directory.Exists(Path.Combine(ctx.Cwd, activeStep.Creates)))
                    AnsiConsole.MarkupLine(
                        $"[yellow]  ⚠ Step {activeStep.Step}: expected '{Markup.Escape(activeStep.Creates)}' was not created.[/]");
            }
            ctx.HaltedAt = (activeStep, total);
            ctx.HaltedRemaining.Clear();
            foreach (var item in ctx.ExecutionQueue) ctx.HaltedRemaining.Enqueue(item);
            ctx.HaltedToolCalls = [.. toolCallsThisTurn];
            ctx.ExecutionQueue.Clear();
            if (ctx.JsonMode)
                ReplJsonBridge.Emit(new { type = "step_status", step = activeStep.Step, total, status = "halted", stepsLeft = 0 });
            else
                AnsiConsole.MarkupLine("[yellow]  Plan halted. Run /recover to let the agent diagnose and retry, or /resume to retry directly.[/]");
        }
        if (!ctx.JsonMode) AnsiConsole.WriteLine();
        return passed;
    }

    /// <summary>
    /// Transitions the plan into the same recoverable halted state as <see cref="HandleStepResult"/>'s
    /// verify-failure branch, but for a step whose turn never produced a result at all — a
    /// cancelled or unrecoverable streaming request (see the catch blocks in
    /// <c>ReplTurn.StreamTurnResponseAsync</c>). Without this, those failures fell through to a
    /// bare <c>ExecutionQueue.Clear()</c> with <see cref="ReplSessionContext.HaltedAt"/> never
    /// set, silently discarding the rest of the plan with no way for /resume or /recover to act.
    /// </summary>
    internal static void HaltStepOnStreamFailure(
        ReplSessionContext ctx, PlanStep activeStep, int total, IReadOnlyList<string> toolCallsThisTurn)
    {
        ctx.HaltedAt = (activeStep, total);
        ctx.HaltedRemaining.Clear();
        foreach (var item in ctx.ExecutionQueue) ctx.HaltedRemaining.Enqueue(item);
        ctx.HaltedToolCalls = [.. toolCallsThisTurn];
        ctx.ExecutionQueue.Clear();

        _ = ctx.Emitter.EmitAsync(EventTypes.StepHalted, turn: ctx.TurnIndex, payload: new
        {
            step       = activeStep.Step,
            total,
            reason     = "stream_failure",
            tool_calls = toolCallsThisTurn.ToArray(),
        });

        if (ctx.JsonMode)
            ReplJsonBridge.Emit(new { type = "step_status", step = activeStep.Step, total, status = "halted", stepsLeft = 0 });
        else
            AnsiConsole.MarkupLine("[yellow]  Plan halted. Run /recover to let the agent diagnose and retry, or /resume to retry directly.[/]");
    }

    // Read/inspect tools that do not mutate state. When only these are called during a step
    // whose expected tool is a write operation, the agent verified the precondition and
    // determined no action was needed — treat as a conditional skip rather than a failure.
    private static readonly HashSet<string> InspectTools = new(StringComparer.OrdinalIgnoreCase)
    {
        // FileSystem (no prefix)
        "grep_file", "read_file", "list_directory", "list_files",
        "get_file_summary", "get_file_info",
        // Search
        "search_content", "search_symbol", "search_callers",
        // Git
        "git_status", "git_log", "git_diff", "git_show", "git_branch_list", "git_stash_list",
        // Shell (shell_ prefix — get_env and which were stale names)
        "shell_get_env", "shell_which",
    };

    // Returns (Passed, VerifyOutput) where VerifyOutput is the trimmed command output when
    // a verify command ran, or null when the check was purely structural (tool/file presence).
    internal static async Task<(bool Passed, string? VerifyOutput)> VerifyStepAsync(
        PlanStep step, List<string> toolCalls, string cwd,
        CancellationToken cancellationToken = default)
    {
        // No tool calls at all = agent determined nothing needed to be done (conditional skip).
        // Only read/inspect tools called without the expected write tool = agent verified the
        // precondition and determined the action was already done (also a conditional skip).
        // A wrong write tool was called is still a failure.
        var toolOk = step.Tool is null ||
                     toolCalls.Count == 0 ||
                     toolCalls.Any(t => t.Equals(step.Tool, StringComparison.OrdinalIgnoreCase)) ||
                     toolCalls.All(t => InspectTools.Contains(t));
        var fileOk = step.Creates is null ||
                     File.Exists(Path.Combine(cwd, step.Creates)) ||
                     Directory.Exists(Path.Combine(cwd, step.Creates));

        if (!toolOk || !fileOk) return (false, null);
        if (step.Verifies is null)  return (true, null);

        return await RunVerifyCommandAsync(step.Verifies, cwd, cancellationToken);
    }

    private static async Task<(bool Succeeded, string? Output)> RunVerifyCommandAsync(
        string command, string cwd, CancellationToken cancellationToken)
    {
        const int MaxVerifyOutputChars = 300;
        try
        {
            var result = await (OperatingSystem.IsWindows()
                ? fuseraft.Infrastructure.Plugins.ProcessHelper.RunAsync(
                    "cmd.exe",   ["/c", command], workingDirectory: cwd, timeoutSeconds: 10, cancellationToken: cancellationToken)
                : fuseraft.Infrastructure.Plugins.ProcessHelper.RunAsync(
                    "/bin/bash", ["-c", command], workingDirectory: cwd, timeoutSeconds: 10, cancellationToken: cancellationToken));
            var raw = result.ToPluginOutput();
            var output = raw.Length > MaxVerifyOutputChars
                ? raw[..MaxVerifyOutputChars] + $"…[{raw.Length - MaxVerifyOutputChars} chars truncated]"
                : raw;
            return (result.Succeeded, string.IsNullOrWhiteSpace(output) ? null : output);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    internal static bool TryParsePlan(string text, out PlanStep[] steps) =>
        PlanStep.TryParse(text, out steps);
}
