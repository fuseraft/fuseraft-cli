using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Cli.Display;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands.Repl;

internal static class ReplTurn
{
    internal static readonly string[] SpinnerFrames = OperatingSystem.IsWindows()
        ? ["-", "\\", "|", "/"]
        : ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    internal const int ContextTokenBudget = 80_000;
    internal const int StepIterationLimit = 5;

    // Maximum times a transient streaming error (ResponseEnded, IOException, TimeoutException)
    // is retried automatically before surfacing the failure to the user.
    private const int MaxStreamRetries = 2;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> (or any inner exception) looks like a
    /// transient mid-stream disconnection that is worth retrying automatically — e.g. the
    /// server closed the SSE connection before the response was complete, a network hiccup
    /// reset the TCP connection, or the per-stream idle timeout fired.
    /// Auth errors, context-overflow errors, and user cancellations are <b>not</b> transient
    /// and must not be retried here.
    /// </summary>
    private static bool IsTransientStreamError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException) return false; // user-initiated — never retry
            var msg = e.Message;
            if (msg.Contains("ResponseEnded",        StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("response ended",       StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("stream was closed",    StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("connection was reset", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("forcibly closed",      StringComparison.OrdinalIgnoreCase))
                return true;
            if (e is IOException or TimeoutException) return true;
        }
        return false;
    }

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
                var stepMsg = BuildStepMessage(step, total);
                if (ctx.RecoveryHint is not null)
                {
                    stepMsg = ctx.RecoveryHint + "\n\n" + stepMsg;
                    ctx.RecoveryHint = null;
                }
                var historyMarker = ctx.History.Count;
                var passed = await ExecuteAsync(
                    ctx,
                    stepMsg,
                    isStepRequest: true,
                    capturePlan:   false,
                    activeStep:    step,
                    cancellationToken,
                    stepTotal:     total);
                if (passed)
                {
                    // Trim step messages (user prompt + agent response) and replace with a
                    // compact summary so each subsequent step gets a clean, focused context.
                    while (ctx.History.Count > historyMarker)
                        ctx.History.RemoveAt(historyMarker);
                    ctx.History.Add(new ChatMessage(ChatRole.User,
                        $"[Step {step.Step} of {total} complete] {step.Description}"));
                }
                // Checkpoint after every step so a crash mid-plan can be recovered on --resume.
                await SaveSnapshotAsync(ctx);
                continue;
            }

            var turnLabel = (ctx.TurnIndex + 1).ToString();
            if (!ctx.JsonMode)
                AnsiConsole.Markup(ctx.SafeMode
                    ? $"[dim][[safe]] {turnLabel}[/][bold cyan]>[/] "
                    : $"[dim]{turnLabel}[/][bold cyan]>[/] ");

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
                    // In JSON mode stdout must be a clean JSONL stream, so we
                    // redirect both Console.Out and AnsiConsole to a StringWriter
                    // while the command runs, then emit the captured text as a
                    // token event so the webview can render it.
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
                        var captured = StripAnsi(capture.ToString()).Trim();
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

                await ExecuteAsync(
                    ctx,
                    result.InputOverride!,
                    isStepRequest: false,
                    capturePlan:   result.CapturePlan,
                    activeStep:    null,
                    cancellationToken);
                _ = SaveSnapshotAsync(ctx);
                continue;
            }

            if (raw.StartsWith('$'))
            {
                var parts = raw.Split(' ', 2, StringSplitOptions.TrimEntries);
                var slug  = parts[0][1..]; // strip '$'
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

                await ExecuteAsync(ctx, input, isStepRequest: false, capturePlan: false, activeStep: null, cancellationToken);
                _ = SaveSnapshotAsync(ctx);
                continue;
            }

            if (raw.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("quit", StringComparison.OrdinalIgnoreCase))
                break;

            await ExecuteAsync(
                ctx, raw,
                isStepRequest: false, capturePlan: false, activeStep: null,
                cancellationToken);
            _ = SaveSnapshotAsync(ctx);
        }
    }

    internal static async Task SaveSnapshotAsync(ReplSessionContext ctx)
    {
        try
        {
            var snap = ReplSessionSnapshot.Capture(
                ctx.SessionId, ctx.ModelId, ctx.Cwd,
                ctx.TurnIndex, ctx.History, ctx.StartedAt,
                currentPlan:     ctx.CurrentPlan,
                executionQueue:  ctx.ExecutionQueue.Count > 0
                                 ? [.. ctx.ExecutionQueue.Select(x => new PlanStepEntry(x.Step, x.Total))]
                                 : null,
                haltedAt:        ctx.HaltedAt is null ? null
                                 : new PlanStepEntry(ctx.HaltedAt.Value.Step, ctx.HaltedAt.Value.Total),
                haltedRemaining: ctx.HaltedRemaining.Count > 0
                                 ? [.. ctx.HaltedRemaining.Select(x => new PlanStepEntry(x.Step, x.Total))]
                                 : null,
                haltedToolCalls: ctx.HaltedToolCalls.Count > 0
                                 ? [.. ctx.HaltedToolCalls]
                                 : null,
                recoveryHint:    ctx.RecoveryHint);
            await ReplSessionSnapshot.SaveAsync(snap);
        }
        catch { }
    }

    // -------------------------------------------------------------------------
    // Turn execution
    // -------------------------------------------------------------------------

    internal static async Task<bool> ExecuteAsync(
        ReplSessionContext ctx,
        string input,
        bool isStepRequest,
        bool capturePlan,
        PlanStep? activeStep,
        CancellationToken cancellationToken,
        int stepTotal = 0,
        bool isCorrectionTurn = false)
    {
        ctx.Emitter.SetTurn(ctx.TurnIndex);
        await ctx.Emitter.EmitAsync(EventTypes.UserInput, turn: ctx.TurnIndex, payload: new { content = input });
        ctx.History.Add(new ChatMessage(ChatRole.User, input));
        await ctx.Emitter.EmitAsync(EventTypes.TurnStart, turn: ctx.TurnIndex, payload: new { is_step = isStepRequest, is_correction = isCorrectionTurn });

        // Preserve the user's input before the LLM call so a crash mid-turn still
        // leaves a recoverable snapshot with the typed text.
        if (!isStepRequest)
            _ = SaveSnapshotAsync(ctx);

        var sb                = new StringBuilder();
        var toolCallsThisTurn = new List<string>();
        var toolCallDetails   = new List<(string Name, string? Args)>();
        var fileChanges        = new List<(char Sigil, string Path)>();
        var fileChangeSeen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toolRounds        = 0;
        var inToolBatch       = false;
        var textStarted       = false;
        var totalLinesAdvanced = 0;
        var charsOnLine        = 0;
        var termWidth          = Console.IsOutputRedirected ? int.MaxValue : Math.Max(Console.WindowWidth, 1);

        var turnStart = DateTime.UtcNow;
        var reqCts    = new CancellationTokenSource();
        ctx.ActiveCts = reqCts;
        var spinCts   = CancellationTokenSource.CreateLinkedTokenSource(reqCts.Token);
        if (!ctx.JsonMode && !isStepRequest) AnsiConsole.WriteLine();
        var spinTask  = ctx.JsonMode
            ? Task.CompletedTask
            : RunSpinnerAsync(capturePlan ? "planning…" : "thinking…", spinCts.Token, turnStart);
        var spinning  = !ctx.JsonMode;

        // Cancels and awaits the spinner; caller disposes spinCts.
        async Task StopSpinnerAsync()
        {
            if (!spinning) return;
            spinning = false;
            spinCts.Cancel();
            await spinTask;
            ClearSpinnerLine();
        }

        var activeClient  = isStepRequest ? ctx.StepClient : ctx.Client;
        var streamAttempt = 0;
        while (true) // retry loop for transient streaming errors
        {
        try
        {
            await foreach (var chunk in activeClient.GetStreamingResponseAsync(
                ctx.History, ctx.ChatOptions, cancellationToken: reqCts.Token))
            {
                var funcCall = chunk.Contents.OfType<FunctionCallContent>().FirstOrDefault();
                if (funcCall is not null)
                {
                    if (!inToolBatch) { toolRounds++; inToolBatch = true; }
                    toolCallsThisTurn.Add(funcCall.Name);
                    toolCallDetails.Add((funcCall.Name, SummarizeToolArgs(funcCall.Arguments)));
                    TrackFileChange(funcCall.Name, funcCall.Arguments, fileChanges, fileChangeSeen, ctx.Cwd);

                    if (ctx.JsonMode)
                    {
                        // Include arguments so the webview can show them on hover/expand.
                        // Values are typically JsonElement from the model's JSON response and
                        // serialise correctly; null Arguments → omit the field entirely.
                        var args = funcCall.Arguments is { Count: > 0 }
                            ? (object)funcCall.Arguments
                            : null;
                        ReplJsonBridge.Emit(new { type = "tool_call", name = funcCall.Name, args });
                    }
                    else
                    {
                        // If text has already been streamed inline, move to a fresh line
                        // so the spinner doesn't overwrite the last streamed characters.
                        if (textStarted && !Console.IsOutputRedirected)
                        {
                            AnsiConsole.WriteLine();
                            totalLinesAdvanced++;
                            charsOnLine = 0;
                        }

                        // Update spinner label to show the accumulating tool chain live.
                        var chain = toolCallsThisTurn.Count <= 4
                            ? string.Join(" → ", toolCallsThisTurn)
                            : string.Join(" → ", toolCallsThisTurn.TakeLast(4)) +
                              $" (+{toolCallsThisTurn.Count - 4})";
                        spinCts.Cancel();
                        await spinTask;
                        spinCts.Dispose();
                        spinCts  = CancellationTokenSource.CreateLinkedTokenSource(reqCts.Token);
                        var verb = toolCallsThisTurn.Count % 2 == 0 ? "fusing" : "rafting";
                        spinTask = RunSpinnerAsync($"{verb}…  {chain}", spinCts.Token, turnStart);
                        spinning = true;
                    }
                    continue;
                }

                var text = chunk.Text;
                if (string.IsNullOrEmpty(text)) continue;
                inToolBatch = false;
                sb.Append(text);

                if (!capturePlan)
                {
                    if (ctx.JsonMode)
                    {
                        ReplJsonBridge.Emit(new { type = "token", text });
                    }
                    else
                    {
                        if (!textStarted)
                        {
                            textStarted = true;
                            await StopSpinnerAsync();
                            if (!Console.IsOutputRedirected)
                                ClearSpinnerLine();
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine("[dim]fuseraft agent:[/]");
                            totalLinesAdvanced = 0;
                            charsOnLine        = 0;
                        }
                        else if (spinning)
                        {
                            await StopSpinnerAsync();
                            if (!Console.IsOutputRedirected)
                            {
                                if (totalLinesAdvanced > 0)
                                    Console.Write($"\x1b[{totalLinesAdvanced}A");
                                Console.Write("\r\x1b[J");
                            }
                            totalLinesAdvanced = 0;
                            charsOnLine        = 0;
                        }
                        if (!Console.IsOutputRedirected)
                        {
                            foreach (var ch in text)
                            {
                                if (ch == '\n') { totalLinesAdvanced++; charsOnLine = 0; }
                                else if (++charsOnLine >= termWidth) { totalLinesAdvanced++; charsOnLine = 0; }
                            }
                            Console.Write(text);
                        }
                    }
                }
            }
            break; // streaming succeeded — exit retry loop
        }
        catch (OperationCanceledException)
        {
            await StopSpinnerAsync();
            spinCts.Dispose();
            await ctx.Emitter.EmitAsync(EventTypes.Cancelled, turn: ctx.TurnIndex);
            if (ctx.JsonMode)
                ReplJsonBridge.Emit(new { type = "cancelled" });
            else
                AnsiConsole.MarkupLine("[dim](cancelled)[/]");
            if (ctx.History.Count > 0 && ctx.History[^1].Role == ChatRole.User)
                ctx.History.RemoveAt(ctx.History.Count - 1);
            ctx.ExecutionQueue.Clear();
            if (!ctx.JsonMode) AnsiConsole.WriteLine();
            reqCts.Dispose();
            ctx.ActiveCts = null;
            return false;
        }
        catch (Exception ex) when (IsTransientStreamError(ex) && streamAttempt < MaxStreamRetries)
        {
            // Transient stream disconnection — retry automatically with back-off.
            streamAttempt++;
            await StopSpinnerAsync();
            spinCts.Dispose();

            await ctx.Emitter.EmitAsync(EventTypes.ReplError, turn: ctx.TurnIndex, payload: new
            {
                exception_type = ex.GetType().Name,
                message        = ex.Message,
                attempt        = streamAttempt,
                final          = false,
            });

            if (ctx.JsonMode)
                ReplJsonBridge.Emit(new { type = "retrying", attempt = streamAttempt, max = MaxStreamRetries });
            else
                AnsiConsole.MarkupLine(
                    $"[dim]  ↺ {Markup.Escape(ex.Message)} — retrying ({streamAttempt}/{MaxStreamRetries})…[/]");

            // Exponential back-off: 2 s, 4 s. Not wired to the cancellation token so the
            // short sleep is never interrupted — max wasted time is 6 s total.
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, streamAttempt)));

            // Reset per-attempt accumulators before reissuing the request.
            sb.Clear(); toolCallsThisTurn.Clear(); toolCallDetails.Clear();
            fileChanges.Clear(); fileChangeSeen.Clear();
            toolRounds = 0; inToolBatch = false; textStarted = false;
            totalLinesAdvanced = 0; charsOnLine = 0;

            // Restart spinner for the fresh attempt.
            spinCts  = CancellationTokenSource.CreateLinkedTokenSource(reqCts.Token);
            spinTask = ctx.JsonMode
                ? Task.CompletedTask
                : RunSpinnerAsync(capturePlan ? "planning…" : "thinking…", spinCts.Token, turnStart);
            spinning = !ctx.JsonMode;
            // continue while-loop → reissue GetStreamingResponseAsync
        }
        catch (Exception ex)
        {
            await StopSpinnerAsync();
            spinCts.Dispose();

            await ctx.Emitter.EmitAsync(EventTypes.ReplError, turn: ctx.TurnIndex, payload: new
            {
                exception_type = ex.GetType().Name,
                message        = ex.Message,
                attempt        = streamAttempt + 1,
                final          = true,
            });

            if (ctx.JsonMode)
                ReplJsonBridge.Emit(new { type = "error", text = ex.Message });
            else
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            if (ctx.History.Count > 0 && ctx.History[^1].Role == ChatRole.User)
                ctx.History.RemoveAt(ctx.History.Count - 1);
            ctx.ExecutionQueue.Clear();
            reqCts.Dispose();
            ctx.ActiveCts = null;
            return false;
        }
        } // end while (retry loop)

        reqCts.Dispose();
        ctx.ActiveCts = null;
        await StopSpinnerAsync();
        spinCts.Dispose();

        var responseText = sb.ToString();

        if (!capturePlan && responseText.Length > 0 && !ctx.JsonMode)
        {
            if (!textStarted)
            {
                // Nothing was streamed inline — render the full response with Markdown.
                if (!Console.IsOutputRedirected)
                    ClearSpinnerLine();
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[dim]fuseraft agent:[/]");
                AnsiConsole.Write(MarkdownRenderer.Render(responseText));
            }
            else
            {
                if (!Console.IsOutputRedirected)
                {
                    if (totalLinesAdvanced > 0)
                        Console.Write($"\x1b[{totalLinesAdvanced}A");
                    Console.Write("\r\x1b[J");
                }
                AnsiConsole.Write(MarkdownRenderer.Render(responseText));
            }
        }
        if (!ctx.JsonMode) AnsiConsole.WriteLine();
        if (responseText.Length > 0)
            ctx.History.Add(new ChatMessage(ChatRole.Assistant, responseText));
        else if (!capturePlan)
        {
            // The model returned zero content — surface a clear warning so the user
            // knows to retry rather than wondering why the prompt went quiet.
            if (ctx.JsonMode)
                ReplJsonBridge.Emit(new { type = "warning", text = "Model returned an empty response. Try sending your message again." });
            else
                AnsiConsole.MarkupLine("[dim]  ↯ empty response — the model returned no content. Try again.[/]");

            await ctx.Emitter.EmitAsync(EventTypes.ReplWarning, turn: ctx.TurnIndex, payload: new
            {
                message = "empty_response",
            });
        }

        if (capturePlan && responseText.Length > 0)
            HandlePlanCapture(ctx, responseText);

        bool stepPassed = true;
        if (isStepRequest && activeStep is not null)
            stepPassed = await HandleStepResult(ctx, activeStep, stepTotal, toolCallsThisTurn,
                hitIterationCap: toolRounds >= StepIterationLimit, responseText, cancellationToken);

        // Free-form turns: if the response claims a mutation but no write tool was called,
        // auto-inject a correction so the agent is required to actually call the tool.
        // On the correction turn itself fall back to a warning to avoid infinite recursion.
        if (!isStepRequest && !capturePlan && responseText.Length > 0 &&
            !toolCallsThisTurn.Any(t => MutationTools.Contains(t)) &&
            ContainsMutationClaim(responseText))
        {
            if (!isCorrectionTurn)
            {
                await ctx.Emitter.EmitAsync(EventTypes.CorrectionInjected, turn: ctx.TurnIndex, payload: new { reason = "mutation_claimed_without_write_tool" });
                if (!ctx.JsonMode)
                    AnsiConsole.MarkupLine("[dim]  ↺ mutation claimed without write tool — injecting correction[/]");
                const string correctionMsg =
                    "You described changes above but did not call any write tool. " +
                    "Please call write_file or patch_file now to actually apply the changes. " +
                    "Do not re-describe the changes — just call the tool.";
                await ExecuteAsync(
                    ctx, correctionMsg,
                    isStepRequest: false, capturePlan: false, activeStep: null,
                    cancellationToken, isCorrectionTurn: true);
            }
            else
            {
                if (!ctx.JsonMode)
                    AnsiConsole.MarkupLine(
                        "[yellow]  ⚠ No write tool called after correction — verify the agent did not fabricate this result.[/]");
            }
        }

        var postEst = ctx.EstimateTokens();
        if (ctx.PrevTurnTokenEstimate > 0)
            ctx.TurnTokenDeltas.Add(postEst - ctx.PrevTurnTokenEstimate);
        ctx.PrevTurnTokenEstimate = postEst;

        // Compact status line after each free-form response.
        if (!ctx.JsonMode && !isStepRequest && !capturePlan && responseText.Length > 0 && !Console.IsOutputRedirected)
        {
            var elapsed    = DateTime.UtcNow - turnStart;
            var elapsedStr = elapsed.TotalSeconds >= 1 ? $" · {(int)elapsed.TotalSeconds}s" : string.Empty;
            var toolStr    = toolCallsThisTurn.Count > 0
                ? $" · {toolCallsThisTurn.Count} tool{(toolCallsThisTurn.Count == 1 ? "" : "s")}"
                : string.Empty;
            AnsiConsole.MarkupLine(
                $"[dim]  ── turn {ctx.TurnIndex + 1} · ~{postEst:N0} tok{toolStr}{elapsedStr}[/]");
            foreach (var (sigil, path) in fileChanges)
            {
                var sigilColor = sigil == 'D' ? "red" : sigil == 'A' ? "green" : "yellow";
                AnsiConsole.MarkupLine($"  [{sigilColor}]{sigil}[/] [dim]{Markup.Escape(path)}[/]");
            }
        }

        // One-time 75 % context warning. Fires on free-form turns only (not
        // plan steps or plan-capture) so it never interrupts /execute flow.
        // Resets after /compact or /clear so it can fire once per "fill cycle".
        if (!ctx.ContextWarningShown && !isStepRequest && !capturePlan && responseText.Length > 0)
        {
            var pct = (double)postEst / ContextTokenBudget;
            if (pct >= 0.75)
            {
                ctx.ContextWarningShown = true;
                await ctx.Emitter.EmitAsync(EventTypes.ContextWarning, turn: ctx.TurnIndex, payload: new
                {
                    estimated_tokens = postEst,
                    budget           = ContextTokenBudget,
                    pct              = Math.Round(pct, 3),
                });
                if (ctx.JsonMode)
                    ReplJsonBridge.Emit(new
                    {
                        type = "warning",
                        text = $"Context is {pct:P0} full. Consider /compact to summarise and free space.",
                    });
                else
                    AnsiConsole.MarkupLine(
                        $"[dim yellow]  ⚠ Context {pct:P0} full — consider [/][bold]/compact[/]" +
                        $"[dim yellow] to summarise and free space.[/]");
            }
        }

        if (TrimHistory(ctx.History))
        {
            if (!ctx.JsonMode)
                AnsiConsole.MarkupLine("[dim]  (old messages trimmed to fit context window)[/]");
        }

        if (!ctx.JsonMode && ctx.Verbose)
            AnsiConsole.MarkupLine(
                $"[dim]  tokens (est.): {postEst:N0} / {ContextTokenBudget:N0}  rounds: {toolRounds}  tool calls: {toolCallsThisTurn.Count}[/]");

        foreach (var (name, args) in toolCallDetails)
            await ctx.Emitter.EmitAsync(EventTypes.ToolCall, turn: ctx.TurnIndex, payload: new { tool_name = name, args });
        await ctx.Emitter.EmitAsync(EventTypes.AssistantResponse, turn: ctx.TurnIndex, payload: new { content = responseText });
        await ctx.Emitter.EmitAsync(EventTypes.TurnEnd, turn: ctx.TurnIndex, payload: new
        {
            elapsed_ms       = (int)(DateTime.UtcNow - turnStart).TotalMilliseconds,
            estimated_tokens = postEst,
            tool_rounds      = toolRounds,
            tool_count       = toolCallsThisTurn.Count,
            is_step          = isStepRequest,
            is_correction    = isCorrectionTurn,
        });

        if (ctx.PendingSave && responseText.Length > 0)
        {
            UserConfigStore.Save(ctx.UserCfg!);
            if (!ctx.JsonMode)
            {
                AnsiConsole.MarkupLine($"[dim]Settings saved to[/] [bold]{Markup.Escape(UserConfigStore.ConfigPath)}[/]");
                AnsiConsole.MarkupLine($"[dim]API key stored in[/] [bold]{Markup.Escape(ctx.KeyStore.StoreName)}[/]");
            }
            ctx.PendingSave = false;
        }

        if (ctx.JsonMode)
        {
            if (fileChanges.Count > 0)
                ReplJsonBridge.Emit(new
                {
                    type    = "file_changes",
                    changes = fileChanges.Select(f => new { sigil = f.Sigil.ToString(), path = f.Path }).ToArray(),
                });
            ReplJsonBridge.Emit(new { type = "message_end", turnIndex = ctx.TurnIndex, toolCalls = toolCallsThisTurn.ToArray() });
        }

        ctx.TurnIndex++;
        return stepPassed;
    }

    internal static void HandlePlanCapture(ReplSessionContext ctx, string responseText)
    {
        if (TryParsePlan(responseText, out var steps) && steps.Length > 0)
        {
            ctx.CurrentPlan = steps;
            _ = ctx.Emitter.EmitAsync(EventTypes.PlanCaptured, turn: ctx.TurnIndex, payload: new { step_count = steps.Length });
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
        ReplSessionContext ctx, PlanStep activeStep, int total, List<string> toolCallsThisTurn, bool hitIterationCap,
        string responseText = "", CancellationToken cancellationToken = default)
    {
        var passed    = await VerifyStepAsync(activeStep, toolCallsThisTurn, ctx.Cwd, cancellationToken);
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
            await ctx.Emitter.EmitAsync(EventTypes.StepComplete, turn: ctx.TurnIndex, payload: new
            {
                step       = activeStep.Step,
                total,
                skipped,
                steps_left = stepsLeft,
                hit_iteration_cap = hitIterationCap,
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
                        $"[dim]  ↯ Step {activeStep.Step} reached the {StepIterationLimit}-round limit; later calls in this step may have been cut short.[/]");
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
            });
            if (!ctx.JsonMode)
            {
                if (activeStep.Tool is not null &&
                    !toolCallsThisTurn.Any(t => t.Equals(activeStep.Tool, StringComparison.OrdinalIgnoreCase)))
                {
                    if (hitIterationCap)
                        AnsiConsole.MarkupLine(
                            $"[yellow]  ⚠ Step {activeStep.Step}: hit the {StepIterationLimit}-round limit before " +
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

    internal static async Task ExtractMemoriesOnExitAsync(ReplSessionContext ctx)
    {
        if (ctx.TurnIndex == 0 || ctx.LastExtractedTurnIndex == ctx.TurnIndex) return;
        try
        {
            if (!ctx.JsonMode) AnsiConsole.Markup("[dim]saving memory…[/]");
            var mc = ctx.Factory.Create(ctx.ModelConfig);
            using var _ = mc as IDisposable;
            var existing = await ctx.MemoryStore.LoadAllAsync(ctx.Cwd, ctx.SessionId);
            var (saved, parseFailed) = await new MemoryExtractor(mc).ExtractAsync([.. ctx.History], existing);
            if (!ctx.JsonMode) Console.Write($"\r{new string(' ', 30)}\r");
            foreach (var m in saved) await ctx.MemoryStore.SaveAsync(m, ctx.Cwd, sessionId: ctx.SessionId);
            if (!ctx.JsonMode)
            {
                if (parseFailed)
                    AnsiConsole.MarkupLine("[dim](memory extraction returned unparseable output)[/]");
                else if (saved.Count > 0)
                    AnsiConsole.MarkupLine(
                        $"[dim]Memory: {saved.Count} entr{(saved.Count == 1 ? "y" : "ies")} saved.[/]");
            }
        }
        catch
        {
            if (!ctx.JsonMode) Console.Write($"\r{new string(' ', 30)}\r");
        }
    }

    // -------------------------------------------------------------------------
    // Static utilities
    // -------------------------------------------------------------------------

    internal static bool TrimHistory(List<ChatMessage> history)
    {
        static int Estimate(ChatMessage m) => (m.Text?.Length ?? 0) / 4;

        var total = history.Sum(Estimate);
        if (total <= ContextTokenBudget) return false;

        int start = history.Count > 0 && history[0].Role == ChatRole.System ? 1 : 0;
        while (total > ContextTokenBudget && start + 1 < history.Count)
        {
            // Remove one message per iteration across all roles that form a turn:
            // user prompt, interleaved assistant tool-call stubs, tool results
            // (ChatRole.Tool), and the final assistant reply are all evicted together
            // as the loop advances through the turn sequence.
            var role = history[start].Role;
            if (role == ChatRole.User || role == ChatRole.Assistant || role == ChatRole.Tool)
            {
                total -= Estimate(history[start]);
                history.RemoveAt(start);
            }
            else
            {
                start++; // unexpected role — advance to avoid an infinite loop
            }
        }
        return true;
    }

    internal static string BuildStepMessage(PlanStep step, int total)
    {
        var sb = new StringBuilder();
        sb.Append($"Execute step {step.Step} of {total}: {step.Description}");
        if (step.Tool      is not null) sb.Append($"\nExpected tool: {step.Tool}");
        if (step.Creates   is not null) sb.Append($"\nExpected artifact: {step.Creates}");
        if (step.Verifies  is not null) sb.Append($"\nVerification command (must exit 0): {step.Verifies}");
        if (step.DependsOn is { Length: > 0 })
            sb.Append($"\nDepends on: steps {string.Join(", ", step.DependsOn)} (already completed)");
        if (step.Tool is not null)
            sb.Append($"\n\nYou MUST call '{step.Tool}' for this step. Do NOT call any other tool that modifies files or state. Do NOT do work that belongs to a later step.");
        else
            sb.Append("\n\nCall only the tools needed for this step. Do NOT do work that belongs to a later step.");
        sb.Append("\n\nCRITICAL: Never fabricate or simulate tool output. Do not describe what the result would look like. If the action is genuinely not needed, say so in one sentence and call no tool. Otherwise, call the tool — the actual output will be shown automatically.");
        return sb.ToString();
    }

    // Read/inspect tools that do not mutate state. When only these are called during a step
    // whose expected tool is a write operation, the agent verified the precondition and
    // determined no action was needed — treat as a conditional skip rather than a failure.
    private static readonly HashSet<string> InspectTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "grep_file", "read_file", "list_directory",
        "search_files", "search_content",
        "git_status", "git_log", "git_diff",
        "get_env", "which",
    };

    // Write-class tools whose presence confirms the agent actually mutated state.
    // When none appear in a turn that contains mutation-claim language the agent may
    // have fabricated output — see the post-turn check in ExecuteAsync.
    private static readonly HashSet<string> MutationTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "patch_file", "create_directory", "delete_file",
        "move_file", "copy_file", "set_permissions", "shell_run",
        "git_commit", "git_add", "git_rebase",
    };

    // Matches "I updated", "I've created", "I have fixed", "I just patched", etc.
    // First-person anchor prevents false positives when the agent is describing tool failures
    // or analysing third-party content that happens to mention file paths and past-tense verbs.
    private static readonly Regex FirstPersonMutationRegex = new(
        @"\bI(?:'ve| have| just)?\s+(updated|created|fixed|modified|patched|deleted|saved|written)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool ContainsMutationClaim(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!FirstPersonMutationRegex.IsMatch(text)) return false;
        // Require a file-like reference so purely conversational "I fixed the explanation" doesn't fire.
        var lower = text.ToLowerInvariant();
        return lower.Contains('/') || lower.Contains('\\') ||
               lower.Contains(".md") || lower.Contains(".cs")   || lower.Contains(".py")  ||
               lower.Contains(".js") || lower.Contains(".ts")   || lower.Contains(".json") ||
               lower.Contains(".xml") || lower.Contains(".yaml") || lower.Contains(".txt") ||
               lower.Contains(".drawio") || lower.Contains(".sh") || lower.Contains(".toml") ||
               lower.Contains(".go")  || lower.Contains(".java") || lower.Contains(".rb")  ||
               lower.Contains(".rs")  || lower.Contains(".cpp")  || lower.Contains(".c")   ||
               lower.Contains(".h")   || lower.Contains(".html") || lower.Contains(".css") ||
               lower.Contains(".vue") || lower.Contains(".kt")   || lower.Contains(".swift");
    }

    internal static async Task<bool> VerifyStepAsync(
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

        if (!toolOk || !fileOk) return false;
        if (step.Verifies is null)  return true;

        return await RunVerifyCommandAsync(step.Verifies, cwd, cancellationToken);
    }

    private static async Task<bool> RunVerifyCommandAsync(string command, string cwd, CancellationToken cancellationToken)
    {
        try
        {
            var result = await (OperatingSystem.IsWindows()
                ? fuseraft.Infrastructure.Plugins.ProcessHelper.RunAsync(
                    "cmd.exe",   ["/c", command], workingDirectory: cwd, timeoutSeconds: 10, cancellationToken: cancellationToken)
                : fuseraft.Infrastructure.Plugins.ProcessHelper.RunAsync(
                    "/bin/bash", ["-c", command], workingDirectory: cwd, timeoutSeconds: 10, cancellationToken: cancellationToken));
            return result.Succeeded;
        }
        catch { return false; }
    }

    internal static bool TryParsePlan(string text, out PlanStep[] steps) =>
        PlanStep.TryParse(text, out steps);

    private static string BuildToolSummary(List<string> toolCalls)
    {
        int reads = 0, searches = 0, writes = 0, shell = 0, git = 0, skills = 0, other = 0;
        foreach (var name in toolCalls)
        {
            var n = name.Replace("_", "").ToLowerInvariant();
            if (n is "readfile" or "listdirectory" or "listfiles" or "grepfile"
                    or "getfilesummary" or "getfileinfo")
                reads++;
            else if (n.StartsWith("search"))
                searches++;
            else if (n is "writefile" or "patchfile" or "createdirectory"
                    or "deletefile" or "deletedirectory" or "copyfile" or "movefile")
                writes++;
            else if (n.StartsWith("shell"))
                shell++;
            else if (n.StartsWith("git"))
                git++;
            else if (n is "loadskill")
                skills++;
            else
                other++;
        }
        var parts = new List<string>();
        if (reads    > 0) parts.Add($"{reads} read{(reads    == 1 ? "" : "s")}");
        if (searches > 0) parts.Add($"{searches} search{(searches == 1 ? "" : "es")}");
        if (writes   > 0) parts.Add($"{writes} write{(writes   == 1 ? "" : "s")}");
        if (shell    > 0) parts.Add($"{shell} shell");
        if (git      > 0) parts.Add($"{git} git");
        if (skills   > 0) parts.Add($"{skills} skill{(skills   == 1 ? "" : "s")}");
        if (other    > 0) parts.Add($"{other} other");
        var total  = toolCalls.Count;
        var detail = parts.Count > 1 ? $"  ({string.Join(" · ", parts)})" : string.Empty;
        return $"{total} tool{(total == 1 ? "" : "s")}{detail}";
    }

    private static void TrackFileChange(
        string toolName,
        IDictionary<string, object?>? args,
        List<(char Sigil, string Path)> fileChanges,
        HashSet<string> seen,
        string cwd)
    {
        var n = toolName.Replace("_", "").ToLowerInvariant();
        string? rawPath;
        char sigil;
        if (n is "writefile" or "patchfile")
        {
            rawPath = GetArg(args, "path");
            var abs = rawPath is null ? null
                : Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(cwd, rawPath);
            sigil = abs is not null && File.Exists(abs) ? 'M' : 'A';
        }
        else if (n is "createdirectory")  { rawPath = GetArg(args, "path");                                 sigil = 'A'; }
        else if (n is "deletefile" or "deletedirectory") { rawPath = GetArg(args, "path");                  sigil = 'D'; }
        else if (n is "copyfile")         { rawPath = GetArg(args, "destination") ?? GetArg(args, "path");  sigil = 'A'; }
        else if (n is "movefile")         { rawPath = GetArg(args, "destination");                          sigil = 'M'; }
        else return;
        if (string.IsNullOrWhiteSpace(rawPath)) return;
        var display = MakeRelativePath(rawPath, cwd);
        if (seen.Add(display))
            fileChanges.Add((sigil, display));
    }

    private static string? GetArg(IDictionary<string, object?>? args, string key)
    {
        if (args is null) return null;
        return args.TryGetValue(key, out var v) ? v?.ToString() : null;
    }

    private static string MakeRelativePath(string path, string cwd)
    {
        try
        {
            var abs = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(cwd, path));
            if (abs.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
            {
                var rel = abs[cwd.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.IsNullOrEmpty(rel) ? abs : rel;
            }
            return abs;
        }
        catch { return path; }
    }

    private static string? SummarizeToolArgs(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return null;
        ReadOnlySpan<string> priority = ["path", "command", "script", "url", "key", "query", "message", "branch"];
        foreach (var key in priority)
        {
            if (args.TryGetValue(key, out var val) && val is not null)
            {
                var s = val.ToString() ?? string.Empty;
                return $"{key}={(s.Length > 60 ? s[..60] : s)}";
            }
        }
        var first = args.First();
        var fv = first.Value?.ToString() ?? string.Empty;
        return $"{first.Key}={(fv.Length > 60 ? fv[..60] : fv)}";
    }

    // Drip-prints text character by character so large chunks don't pop in all at once.
    // Skips the delay when output is redirected (e.g. piped to a file).
    internal static async Task WriteChunkSmoothAsync(string text, CancellationToken ct)
    {
        if (Console.IsOutputRedirected || text.Length == 0)
        {
            Console.Write(text);
            return;
        }
        foreach (var ch in text)
        {
            Console.Write(ch);
            await Task.Delay(2, ct);
        }
    }

    internal static async Task RunSpinnerAsync(string label, CancellationToken ct, DateTime? startedAt = null)
    {
        var i = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var elapsed = startedAt.HasValue
                    ? $" ({(int)(DateTime.UtcNow - startedAt.Value).TotalSeconds}s)"
                    : string.Empty;
                var frame = SpinnerFrames[i % SpinnerFrames.Length];
                var text  = $"{frame} {label}{elapsed}";

                // Clamp to one terminal line so the text never wraps. When a line wraps,
                // the subsequent \r\x1b[2K only clears the continuation line and leaves
                // the first visual line as a ghost — producing the multi-line cascade.
                // Guard against Console.WindowWidth failing on non-interactive consoles.
                if (!Console.IsOutputRedirected)
                {
                    var width = 0;
                    try { width = Console.WindowWidth; } catch { }
                    if (width > 4 && text.Length > width - 1)
                        text = text[..(width - 2)] + "…";
                }

                // \r   — move to column 0
                // \x1b[2K — erase entire line (prevents leftover chars when label shrinks)
                Console.Write($"\r\x1b[2K\x1b[2m{text}\x1b[0m");
                i++;
                await Task.Delay(80, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal static void ClearSpinnerLine()
    {
        Console.Write("\r\x1b[2K");
    }

    // Strips ANSI escape sequences (CSI colour codes, OSC sequences, etc.)
    // from text captured while AnsiConsole runs in no-colour mode.  The
    // pattern is intentionally broad so residual escape bytes do not leak
    // into the JSON token emitted to the webview.
    private static readonly Regex _ansiPattern =
        new(@"\x1b(?:\[[^m]*m|\][^\x07]*\x07|[()][AB012]|[=>])", RegexOptions.Compiled);

    internal static string StripAnsi(string text) => _ansiPattern.Replace(text, string.Empty);
}
