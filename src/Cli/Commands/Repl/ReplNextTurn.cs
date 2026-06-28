using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Experimental next-generation REPL turn loop.
/// Shares all business logic with <see cref="ReplTurn"/>; only the terminal
/// rendering is different.  Enable via <c>FUSERAFT_REPL_NEXT=1</c>.
/// </summary>
internal static class ReplNextTurn
{
    // -------------------------------------------------------------------------
    // REPL loop
    // -------------------------------------------------------------------------

    internal static async Task RunAsync(ReplSessionContext ctx, CancellationToken cancellationToken)
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        try   { await RunLoopAsync(ctx, cancellationToken); }
        finally { Console.CancelKeyPress -= OnCancelKeyPress; }

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            var c = ctx.ActiveCts;
            if (c is not null && !c.IsCancellationRequested)
            {
                e.Cancel = true;
                c.Cancel();
            }
        }
    }

    private static async Task RunLoopAsync(ReplSessionContext ctx, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (ctx.ExecutionQueue.Count > 0)
            {
                var (step, total) = ctx.ExecutionQueue.Dequeue();
                var stepMsg = ReplTurn.BuildStepMessage(step, total);
                if (ctx.RecoveryHint is not null)
                {
                    stepMsg = ctx.RecoveryHint + "\n\n" + stepMsg;
                    ctx.RecoveryHint = null;
                }
                var historyMarker = ctx.History.Count;
                var passed = await ReplTurn.ExecuteAsync(
                    ctx,
                    stepMsg,
                    isStepRequest: true,
                    capturePlan:   false,
                    activeStep:    step,
                    cancellationToken,
                    stepTotal:     total);
                if (passed)
                {
                    while (ctx.History.Count > historyMarker)
                        ctx.History.RemoveAt(historyMarker);
                    ctx.History.Add(new ChatMessage(ChatRole.User,
                        $"[Step {step.Step} of {total} complete] {step.Description}"));
                }
                await ReplTurn.SaveSnapshotAsync(ctx);
                continue;
            }

            var turnLabel = (ctx.TurnIndex + 1).ToString();
            if (!ctx.JsonMode)
                AnsiConsole.Markup(ctx.SafeMode
                    ? $"[dim]{turnLabel}[/] [yellow]›[/] "
                    : $"[dim]{turnLabel}[/] [cyan]›[/] ");

            string? raw;
            try   { raw = ctx.JsonMode ? ReplJsonBridge.ReadInput() : ctx.LineReader.ReadLine(); }
            catch (OperationCanceledException) { break; }

            if (raw is null) break;
            raw = raw.Trim();
            if (string.IsNullOrEmpty(raw)) continue;

            if (raw.StartsWith('/'))
            {
                var parts   = raw.Split(' ', 2, StringSplitOptions.TrimEntries);
                var command = parts[0].ToLowerInvariant();
                var arg     = parts.Length > 1 ? parts[1] : string.Empty;

                CommandResult result;
                if (ctx.JsonMode)
                {
                    using var capture    = new StringWriter();
                    var savedOut         = Console.Out;
                    var savedAnsiConsole = AnsiConsole.Console;
                    Console.SetOut(capture);
                    AnsiConsole.Console  = AnsiConsole.Create(new AnsiConsoleSettings
                    {
                        Out         = new AnsiConsoleOutput(capture),
                        ColorSystem = ColorSystemSupport.NoColors,
                        Ansi        = AnsiSupport.No,
                    });
                    try
                    {
                        result = await ReplCommands.HandleAsync(ctx, command, arg, cancellationToken);
                    }
                    finally
                    {
                        Console.SetOut(savedOut);
                        AnsiConsole.Console = savedAnsiConsole;
                        var captured = ReplTurn.StripAnsi(capture.ToString()).Trim();
                        if (!string.IsNullOrWhiteSpace(captured))
                            ReplJsonBridge.Emit(new { type = "token", text = captured });
                    }
                }
                else
                {
                    result = await ReplCommands.HandleAsync(ctx, command, arg, cancellationToken);
                    AnsiConsole.WriteLine();
                }

                if (result.Outcome == CommandOutcome.Exit) break;
                if (result.Outcome == CommandOutcome.Continue)
                {
                    if (ctx.JsonMode)
                        ReplJsonBridge.Emit(new { type = "message_end", turnIndex = ctx.TurnIndex, toolCalls = Array.Empty<string>() });
                    continue;
                }

                await ReplTurn.ExecuteAsync(
                    ctx,
                    result.InputOverride!,
                    isStepRequest: false,
                    capturePlan:   result.CapturePlan,
                    activeStep:    null,
                    cancellationToken);
                _ = ReplTurn.SaveSnapshotAsync(ctx);
                continue;
            }

            if (raw.StartsWith('$'))
            {
                var parts = raw.Split(' ', 2, StringSplitOptions.TrimEntries);
                var slug  = parts[0][1..];
                var args  = parts.Length > 1 ? parts[1] : string.Empty;

                if (ctx.SkillsPlugin is null || !ctx.SkillsPlugin.HasSkill(slug))
                {
                    var available = ctx.SkillsPlugin is not null
                        ? $"Available: {string.Join(", ", ctx.SkillsPlugin.Slugs.Take(10))}"
                        : "No skills are loaded in this session.";
                    var errMsg = string.IsNullOrEmpty(slug)
                        ? $"Usage: $<skill-name> [args]. {available}"
                        : $"Skill '{slug}' not found. {available}";
                    if (ctx.JsonMode)
                        ReplJsonBridge.Emit(new { type = "error", text = errMsg });
                    else
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(errMsg)}[/]");
                    continue;
                }

                var skillContent = await ctx.SkillsPlugin.LoadSkillAsync(slug, cancellationToken);
                var input = string.IsNullOrEmpty(args) ? skillContent : $"{skillContent}\n\n{args}";

                await ReplTurn.ExecuteAsync(ctx, input, isStepRequest: false, capturePlan: false, activeStep: null, cancellationToken);
                _ = ReplTurn.SaveSnapshotAsync(ctx);
                continue;
            }

            if (raw.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("quit", StringComparison.OrdinalIgnoreCase))
                break;

            await ReplTurn.ExecuteAsync(
                ctx, raw,
                isStepRequest: false, capturePlan: false, activeStep: null,
                cancellationToken);
            _ = ReplTurn.SaveSnapshotAsync(ctx);
        }
    }
}
