using System.Text.RegularExpressions;
using Spectre.Console;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>How a <c>/goal</c> run ended — kept on the session so <c>/goal resume</c> knows what to pick up.</summary>
internal enum GoalEnd { Complete, Capped, Stalled, Blocked, Interrupted, JudgeFailed }

/// <summary>The most recent finished <c>/goal</c> run: enough to report on it and to resume it.</summary>
internal sealed record GoalRecord(string Objective, GoalEnd End, int Audits, GoalVerdict? LastVerdict);

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /goal
    // -------------------------------------------------------------------------

    private static readonly string GoalUsage =
        "Usage: /goal [--max N] <objective>   — work until an independent audit confirms the objective is met\n" +
        "       /goal resume [--max N]        — pick up the last goal that did not finish\n" +
        $"Default budget is {ReplGoal.DefaultMaxIterations} audits.";

    private static async Task<CommandResult> CmdGoalAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        if (!TryParseGoalArgs(arg, out var maxIterations, out var rest, out var error))
        {
            GoalSay(ctx, error!, "yellow");
            return CommandResult.Continue;
        }

        if (string.IsNullOrWhiteSpace(rest))
        {
            GoalSay(ctx, GoalUsage, "dim");
            if (ctx.LastGoal is { } last)
                GoalSay(ctx, $"Last goal: {last.Objective} — {DescribeGoalEnd(last)}", "dim");
            return CommandResult.Continue;
        }

        string objective, firstMessage;
        if (rest.Equals("resume", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.LastGoal is not { End: not GoalEnd.Complete } last)
            {
                GoalSay(ctx, "No unfinished goal to resume. Start one with /goal <objective>.", "dim");
                return CommandResult.Continue;
            }
            objective    = last.Objective;
            firstMessage = ReplGoal.BuildResumeMessage(objective);
        }
        else
        {
            objective    = rest;
            firstMessage = objective;
        }

        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/goal", objective, max_iterations = maxIterations });
        await RunGoalLoopAsync(ctx, new GoalState(objective, maxIterations), firstMessage, cancellationToken);
        return CommandResult.Continue;
    }

    /// <summary>Accepts <c>--max N</c> (or <c>--max=N</c>) as a leading flag; everything after it is the objective.</summary>
    internal static bool TryParseGoalArgs(string arg, out int maxIterations, out string rest, out string? error)
    {
        maxIterations = ReplGoal.DefaultMaxIterations;
        rest          = (arg ?? string.Empty).Trim();
        error         = null;

        var m = Regex.Match(rest, @"^--max(?:=|\s+)(\S+)\s*(.*)$", RegexOptions.Singleline);
        if (!m.Success)
        {
            if (!rest.StartsWith("--max", StringComparison.Ordinal)) return true; // no flag: whole arg is the objective
            error = "--max needs a number, e.g. /goal --max 8 <objective>";
            return false;
        }

        if (!int.TryParse(m.Groups[1].Value, out var n) || n < 1 || n > 50)
        {
            error = "--max must be a whole number between 1 and 50.";
            return false;
        }
        maxIterations = n;
        rest          = m.Groups[2].Value.Trim();
        return true;
    }

    private static async Task RunGoalLoopAsync(
        ReplSessionContext ctx, GoalState goal, string firstMessage, CancellationToken cancellationToken)
    {
        GoalSay(ctx, $"Goal: {goal.Objective}", "dim");
        GoalSay(ctx, $"Working until an independent audit confirms it (up to {goal.MaxIterations} audits — Ctrl+C stops).", "dim");
        await ctx.Emitter.EmitAsync(EventTypes.GoalStarted, turn: ctx.TurnIndex,
            payload: new { objective = goal.Objective, max_iterations = goal.MaxIterations });

        var message = firstMessage;
        GoalEnd end;
        while (true)
        {
            var turnOk = await ReplTurn.ExecuteAsync(
                ctx, message, isStepRequest: false, capturePlan: false, activeStep: null, cancellationToken);
            await ReplTurn.SaveSnapshotAsync(ctx);
            if (!turnOk) { end = GoalEnd.Interrupted; break; }

            var verdict = await AuditGoalAsync(ctx, goal, cancellationToken);
            if (verdict is null) { end = GoalEnd.JudgeFailed; break; }

            var action = goal.Record(verdict);
            await ctx.Emitter.EmitAsync(EventTypes.GoalAudit, turn: ctx.TurnIndex, payload: new
            {
                iteration = goal.Iteration,
                max       = goal.MaxIterations,
                score     = verdict.Score,
                complete  = verdict.Complete,
                blocked   = verdict.Blocked,
                missing   = verdict.Missing,
                action    = action.ToString(),
            });

            if (action != GoalAction.Continue)
            {
                end = action switch
                {
                    GoalAction.Complete => GoalEnd.Complete,
                    GoalAction.Blocked  => GoalEnd.Blocked,
                    GoalAction.Stalled  => GoalEnd.Stalled,
                    _                   => GoalEnd.Capped,
                };
                break;
            }

            GoalSay(ctx,
                $"↻ audit {goal.Iteration}/{goal.MaxIterations} ({verdict.Score:P0}) — not done: {OneLine(verdict.Missing)}",
                "yellow");
            message = ReplGoal.BuildFollowUp(goal.Iteration, verdict.Missing);
        }

        var record = new GoalRecord(goal.Objective, end, goal.Iteration, goal.LastVerdict);
        ctx.LastGoal = record;
        await ctx.Emitter.EmitAsync(EventTypes.GoalEnded, turn: ctx.TurnIndex, payload: new
        {
            objective = goal.Objective,
            end       = end.ToString(),
            audits    = goal.Iteration,
            score     = goal.LastVerdict?.Score,
            missing   = goal.LastVerdict?.Missing,
        });

        var (colour, glyph) = end switch
        {
            GoalEnd.Complete => ("green", "✓"),
            GoalEnd.Blocked  => ("cyan", "?"),
            _                => ("yellow", "⚠"),
        };
        GoalSay(ctx, $"{glyph} Goal {DescribeGoalEnd(record)}", colour);
        if (end is GoalEnd.Capped or GoalEnd.Stalled or GoalEnd.Interrupted or GoalEnd.JudgeFailed)
            GoalSay(ctx, "Run /goal resume to keep going.", "dim");
        if (!ctx.JsonMode) AnsiConsole.WriteLine();
    }

    /// <summary>
    /// One judge call. Returns null when the judge could not run (provider error, Ctrl+C) — the
    /// caller stops the loop rather than guessing, since looping blind or declaring victory on a
    /// failed audit are both worse than handing control back.
    /// </summary>
    private static async Task<GoalVerdict?> AuditGoalAsync(
        ReplSessionContext ctx, GoalState goal, CancellationToken cancellationToken)
    {
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reqCts.CancelAfter(TimeSpan.FromMinutes(3));
        ctx.ActiveCts = reqCts;

        // Spinner would corrupt the JSON-mode stream, so it is terminal-only.
        using var spinCts = CancellationTokenSource.CreateLinkedTokenSource(reqCts.Token);
        var spinTask = ctx.JsonMode
            ? Task.CompletedTask
            : ReplConsole.RunSpinnerAsync("auditing goal…", spinCts.Token, DateTime.UtcNow);

        async Task StopSpinner()
        {
            if (ctx.JsonMode) return;
            spinCts.Cancel();
            await spinTask;
            ReplConsole.ClearSpinnerLine();
        }

        try
        {
            var verdict = await ReplGoal.JudgeAsync(ctx.Client, goal.Objective, ctx.History, reqCts.Token);
            await StopSpinner();
            return verdict;
        }
        catch (OperationCanceledException)
        {
            await StopSpinner();
            GoalSay(ctx, "(audit cancelled)", "dim");
            return null;
        }
        catch (Exception ex)
        {
            await StopSpinner();
            GoalSay(ctx, $"✗ Goal audit failed: {ex.Message}", "red");
            return null;
        }
        finally
        {
            ctx.ActiveCts = null;
        }
    }

    internal static string DescribeGoalEnd(GoalRecord r)
    {
        var audits = $"{r.Audits} audit{(r.Audits == 1 ? "" : "s")}";
        var missing = OneLine(r.LastVerdict?.Missing);
        return r.End switch
        {
            GoalEnd.Complete    => $"complete ({audits}, {r.LastVerdict?.Score:P0} confidence).",
            GoalEnd.Capped      => $"not verified after {audits}. Outstanding: {missing}",
            GoalEnd.Stalled     => $"stalled — the same work was reported missing {GoalState.StallLimit} audits running. Outstanding: {missing}",
            GoalEnd.Blocked     => $"paused — the agent needs your input. {missing}",
            GoalEnd.Interrupted => r.Audits == 0 ? "interrupted before the first audit." : $"interrupted after {audits}.",
            GoalEnd.JudgeFailed => $"stopped — the audit could not run ({audits} completed).",
            _                   => r.End.ToString(),
        };
    }

    private static string OneLine(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "(unspecified)" : Regex.Replace(s.Trim(), @"\s+", " ");

    /// <summary>Terminal: dim/coloured markup. JSON bridge: a plain text event (stdout must stay clean JSONL).</summary>
    private static void GoalSay(ReplSessionContext ctx, string text, string colour)
    {
        if (ctx.JsonMode)
            ReplJsonBridge.Emit(new { type = "text", text });
        else
            AnsiConsole.MarkupLine($"[{colour}]{Markup.Escape(text)}[/]");
    }
}
