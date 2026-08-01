using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Cli.Display;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Drives the REPL's input loop (<see cref="RunAsync"/>/<see cref="RunLoopAsync"/>) and turn
/// execution (<see cref="ExecuteAsync"/>). Every method here is stateless — mutable session
/// state lives entirely in the explicit <see cref="ReplSessionContext"/> parameter, per that
/// class's own design note.
///
/// <para>
/// <b>Collaborators</b> (both in <c>fuseraft.Cli.Commands.Repl</c>): terminal-presentation
/// utilities (spinner, drip-print, ANSI stripping — also reused by sub-agent REPL commands)
/// are owned by <see cref="ReplConsole"/>. Plan-capture and step-verification processing is
/// owned by <see cref="ReplTurnOutcome"/>. <see cref="ExecuteAsync"/>'s own retry/streaming
/// core is <see cref="StreamTurnResponseAsync"/>, a same-class extraction (not a separate
/// collaborator, since it closes tightly over per-turn accumulator state) mirroring
/// <c>SessionRunner</c>'s named-exception-handler pattern.
/// </para>
/// </summary>
internal static class ReplTurn
{
    internal const int StepIterationLimit = 5;

    // Maximum times a transient streaming error (ResponseEnded, IOException, TimeoutException)
    // is retried automatically before surfacing the failure to the user.
    private const int MaxStreamRetries = 2;

    // Matches identify/locate/find-style questions about the codebase so the turn can force a
    // grounding tool call instead of letting the model answer from (possibly fabricated) memory.
    // Live-verified on grok-4.3 (2026-06-30): forcing ChatToolMode.RequireAny for a whole turn
    // does not get the model stuck — it calls a tool once, then still returns normal final text.
    private static readonly Regex ForceEvidenceQuestionPattern = new(
        @"\b(locate|identify)\b" +
        @"|\bwhere\s+(is|are|does|do)\b" +
        @"|\bwhich\s+file\b" +
        @"|\bwhat\s+file\b" +
        @"|\bfind\s+(the|where|which)\b" +
        @"|\bdoes\s+\S.*\bexist\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Returns options forcing at least one tool call for this request when the input looks like
    // an identify/locate-style question and tools are actually available — never mutates the
    // shared ctx.ChatOptions instance, so the override applies to this turn only.
    private static ChatOptions? BuildRequestOptions(ChatOptions? baseOptions, string input)
    {
        if (baseOptions?.Tools is not { Count: > 0 }) return baseOptions;
        if (!ForceEvidenceQuestionPattern.IsMatch(input)) return baseOptions;

        var forced = baseOptions.Clone();
        forced.ToolMode = ChatToolMode.RequireAny;
        return forced;
    }

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

    // Minimum active tool count above which a raw, unclassified 400/413 is plausibly a
    // tool-schema or request-payload rejection rather than a genuine bad request — large
    // REPL tool surfaces (FileSystem + Shell + Search + Git + Session + SubAgent, ~50+
    // schemas) are the likeliest trigger on gateways like Bedrock/LiteLLM.
    private const int LargeToolSurfaceThreshold = 20;

    /// <summary>
    /// Returns a short, plain-text hint when <paramref name="ex"/> looks like a raw HTTP
    /// 400/413 that <see cref="ProviderErrorClassifier"/> could not explain (so
    /// <see cref="FalloverChatClient"/> would not have retried or failed over on it either)
    /// and the active tool count is large enough that a tool-schema/payload rejection is a
    /// plausible cause. Returns <see langword="null"/> when no hint applies — this is a
    /// best-effort diagnostic, not a classification change.
    /// </summary>
    private static string? BuildLargeToolSurfaceHint(Exception ex, int activeToolCount)
    {
        if (activeToolCount < LargeToolSurfaceThreshold) return null;
        if (ProviderErrorClassifier.Classify(ex) != FailoverReason.None) return null;

        for (var e = ex; e is not null; e = e.InnerException)
        {
            int? status = e switch
            {
                ClientResultException cre => cre.Status,
                HttpRequestException { StatusCode: { } sc } => (int)sc,
                _ => null,
            };
            if (status is 400 or 413)
                return $"This may be a tool-schema/payload rejection from the provider — " +
                       $"{activeToolCount} tools are active this turn. Try /tools disable <category>, " +
                       $"or restart with --no-tools to isolate.";
        }
        return null;
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
            else if (ctx.JsonMode)
            {
                // In VS Code mode, never let SIGINT kill the process when there is no
                // active LLM request. Acknowledge with a cancelled event so the webview
                // can re-enable the input field.
                e.Cancel = true;
                ReplJsonBridge.Emit(new { type = "cancelled" });
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
                    if (ctx.LastStepWasInspectOnly)
                    {
                        // Inspect-only step: replace the step prompt with a compact label that
                        // embeds the actual tool outputs so the next step can reference them.
                        var labelSb = new StringBuilder(
                            $"[Step {step.Step} of {total} complete — findings below] {step.Description}");
                        if (ctx.LastStepInspectResults?.Count > 0)
                        {
                            labelSb.AppendLine("\n[Tool outputs:]");
                            foreach (var (toolName, output) in ctx.LastStepInspectResults)
                            {
                                labelSb.AppendLine($"// {toolName}:");
                                labelSb.AppendLine(
                                    output.Length > 4000 ? output[..4000] + "\n…(truncated)" : output);
                            }
                        }
                        if (ctx.History.Count > historyMarker)
                            ctx.History[historyMarker] = new ChatMessage(ChatRole.User, labelSb.ToString());
                        // Trim assistant response — raw outputs are already in the label above.
                        while (ctx.History.Count > historyMarker + 1)
                            ctx.History.RemoveAt(historyMarker + 1);
                    }
                    else
                    {
                        // Write/mutation step: trim everything and leave a compact summary so
                        // each subsequent step gets a clean, focused context.
                        while (ctx.History.Count > historyMarker)
                            ctx.History.RemoveAt(historyMarker);
                        ctx.History.Add(new ChatMessage(ChatRole.User,
                            $"[Step {step.Step} of {total} complete] {step.Description}"));
                    }
                    ctx.LastStepWasInspectOnly = false;
                    ctx.LastStepInspectResults = null;
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

            // Interrupt signal sent via stdin (Windows path: SIGINT can't be used).
            if (ctx.JsonMode && raw == ReplJsonBridge.InterruptToken)
            {
                var c = ctx.ActiveCts;
                if (c is not null && !c.IsCancellationRequested)
                    c.Cancel();
                // If no active request, the signal was stale — silently discard.
                continue;
            }

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
                        var captured = ReplConsole.StripAnsi(capture.ToString()).Trim();
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

        var turnStart = DateTime.UtcNow;
        var stream = await StreamTurnResponseAsync(ctx, input, isStepRequest, capturePlan, turnStart, cancellationToken);
        if (!stream.Success) return false;

        var responseText      = stream.ResponseText;
        var toolCallsThisTurn = stream.ToolCallsThisTurn;
        var fileChanges       = stream.FileChanges;
        var toolRounds        = stream.ToolRounds;
        var capturedResults   = stream.CapturedResults;
        var turnInputTokens   = stream.TurnInputTokens;
        var turnOutputTokens  = stream.TurnOutputTokens;

        responseText = SanitizeAssistantResponse(responseText, out var warningMessage);

        if (!capturePlan && responseText.Length > 0 && !ctx.JsonMode)
        {
            if (!Console.IsOutputRedirected)
                ReplConsole.ClearSpinnerLine();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]fuseraft agent:[/]");
            AnsiConsole.Write(MarkdownRenderer.Render(responseText));
        }
        if (!ctx.JsonMode) AnsiConsole.WriteLine();
        if (responseText.Length > 0)
            ctx.History.Add(new ChatMessage(ChatRole.Assistant, responseText));
        else if (!capturePlan)
        {
            var warningText = warningMessage ?? "Model returned an empty response. Try sending your message again.";

            if (ctx.JsonMode)
                ReplJsonBridge.Emit(new { type = "warning", text = warningText });
            else
                AnsiConsole.MarkupLine($"[dim]  ↯ {Markup.Escape(warningText)}[/]");

            await ctx.Emitter.EmitAsync(EventTypes.ReplWarning, turn: ctx.TurnIndex, payload: new
            {
                message = warningMessage is null ? "empty_response" : "invalid_response_content",
            });
        }

        if (capturePlan && responseText.Length > 0)
            ReplTurnOutcome.HandlePlanCapture(ctx, responseText);

        bool stepPassed = true;
        if (isStepRequest && activeStep is not null)
            stepPassed = await ReplTurnOutcome.HandleStepResult(ctx, activeStep, stepTotal, toolCallsThisTurn,
                capturedResults ?? [], hitIterationCap: toolRounds >= StepIterationLimit,
                responseText, cancellationToken);

        await TryApplyMutationCorrectionAsync(
            ctx, responseText, toolCallsThisTurn, isStepRequest, capturePlan, isCorrectionTurn, cancellationToken);

        await TryApplyCriticReviewAsync(
            ctx, input, responseText, toolCallsThisTurn, isStepRequest, capturePlan, isCorrectionTurn, cancellationToken);

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
            if (ctx.Todo is not null && toolCallsThisTurn.Contains("todo_write", StringComparer.OrdinalIgnoreCase))
            {
                foreach (var item in ctx.Todo.Snapshot())
                {
                    var (glyph, color) = item.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ? ("x", "green")
                        : item.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) ? ("~", "yellow")
                        : (" ", "dim");
                    AnsiConsole.MarkupLine($"  [{color}]{Markup.Escape($"[{glyph}]")}[/] [dim]{Markup.Escape(item.Content)}[/]");
                }
            }
        }

        // One-time 75 % context warning. Fires on free-form turns only (not
        // plan steps or plan-capture) so it never interrupts /execute flow.
        // Resets after /compact or /clear so it can fire once per "fill cycle".
        if (!ctx.ContextWarningShown && !isStepRequest && !capturePlan && responseText.Length > 0)
        {
            var pct = (double)postEst / ctx.ContextTokenBudget;
            if (pct >= 0.75)
            {
                ctx.ContextWarningShown = true;
                await ctx.Emitter.EmitAsync(EventTypes.ContextWarning, turn: ctx.TurnIndex, payload: new
                {
                    estimated_tokens = postEst,
                    budget           = ctx.ContextTokenBudget,
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

        var trimmedCount = TrimHistory(ctx.History, ctx.ContextTokenBudget);
        if (trimmedCount > 0)
        {
            if (!ctx.JsonMode)
                AnsiConsole.MarkupLine("[dim]  (old messages trimmed to fit context window)[/]");
            await ctx.Emitter.EmitAsync(EventTypes.HistoryTrimmed, turn: ctx.TurnIndex,
                payload: new { messages_removed = trimmedCount, estimated_tokens = ctx.EstimateTokens() });
        }

        if (!ctx.JsonMode && ctx.Verbose)
            AnsiConsole.MarkupLine(
                $"[dim]  tokens (est.): {postEst:N0} / {ctx.ContextTokenBudget:N0}  rounds: {toolRounds}  tool calls: {toolCallsThisTurn.Count}[/]");

        await ctx.Emitter.EmitAsync(EventTypes.AssistantResponse, turn: ctx.TurnIndex, payload: new { content = responseText });
        await ctx.Emitter.EmitAsync(EventTypes.TurnEnd, turn: ctx.TurnIndex, payload: new
        {
            elapsed_ms       = (int)(DateTime.UtcNow - turnStart).TotalMilliseconds,
            estimated_tokens = postEst,
            input_tokens     = turnInputTokens  > 0 ? turnInputTokens  : (long?)null,
            output_tokens    = turnOutputTokens > 0 ? turnOutputTokens : (long?)null,
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
                if (ctx.KeyStored)
                    AnsiConsole.MarkupLine($"[dim]API key stored in[/] [bold]{Markup.Escape(ctx.KeyStore.StoreName)}[/]");
            }
            ctx.PendingSave = false;
        }

        if (fileChanges.Count > 0)
        {
            var changeArray = fileChanges.Select(f => new { sigil = f.Sigil.ToString(), path = f.Path }).ToArray();
            await ctx.Emitter.EmitAsync(EventTypes.FileChanges, turn: ctx.TurnIndex, payload: new { changes = changeArray });
            if (ctx.JsonMode)
                ReplJsonBridge.Emit(new { type = "file_changes", changes = changeArray });
        }
        if (ctx.JsonMode)
            ReplJsonBridge.Emit(new { type = "message_end", turnIndex = ctx.TurnIndex, toolCalls = toolCallsThisTurn.ToArray() });

        ctx.TurnIndex++;
        return stepPassed;
    }

    private static string SanitizeAssistantResponse(string responseText, out string? warningMessage)
    {
        var trimmed = responseText.Trim();
        if (trimmed.Length == 0)
        {
            warningMessage = null;
            return string.Empty;
        }

        if (trimmed.StartsWith("to=functions.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Wait must be valid JSON", StringComparison.OrdinalIgnoreCase))
        {
            warningMessage = "Model returned internal tool-call text instead of a user-facing answer. Try again.";
            return string.Empty;
        }

        warningMessage = null;
        return responseText;
    }

    // Free-form turns: if the response claims a mutation but no write tool was called,
    // auto-inject a correction so the agent is required to actually call the tool.
    // On the correction turn itself fall back to a warning to avoid infinite recursion.
    private static async Task TryApplyMutationCorrectionAsync(
        ReplSessionContext ctx,
        string responseText,
        List<string> toolCallsThisTurn,
        bool isStepRequest,
        bool capturePlan,
        bool isCorrectionTurn,
        CancellationToken cancellationToken)
    {
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
    }

    // Free-form turns under adversarial mode: a critic agent reviews the response for
    // fabrication/correctness, same infrastructure /execute steps use. Skipped on the
    // correction turn itself so a rejection can't recurse forever.
    private static async Task TryApplyCriticReviewAsync(
        ReplSessionContext ctx,
        string input,
        string responseText,
        List<string> toolCallsThisTurn,
        bool isStepRequest,
        bool capturePlan,
        bool isCorrectionTurn,
        CancellationToken cancellationToken)
    {
        if (ctx.AdversarialMode && ctx.SubAgent is not null &&
            !isStepRequest && !capturePlan && !isCorrectionTurn && responseText.Length > 0)
        {
            if (!ctx.JsonMode) AnsiConsole.Markup("[dim]  critic reviewing…[/]");
            var (approved, reason) = await ctx.SubAgent.CriticReviewAsync(
                input, expectedTool: null, toolCallsThisTurn, responseText, cancellationToken);
            if (!ctx.JsonMode) Console.Write($"\r{new string(' ', 40)}\r");
            if (!approved)
            {
                await ctx.Emitter.EmitAsync(EventTypes.CorrectionInjected, turn: ctx.TurnIndex, payload: new { reason = "critic_rejected", detail = reason });
                if (!ctx.JsonMode)
                    AnsiConsole.MarkupLine($"[yellow]  ✗ Critic: {Markup.Escape(reason ?? "no reason given")}[/]");
                var correctionMsg =
                    $"A critic reviewed your last response and rejected it: {reason}\n" +
                    "Verify the disputed claim with a tool call and correct your answer. " +
                    "Do not just restate the same claim.";
                await ExecuteAsync(
                    ctx, correctionMsg,
                    isStepRequest: false, capturePlan: false, activeStep: null,
                    cancellationToken, isCorrectionTurn: true);
            }
        }
    }

    // Carrier for the outcome of streaming one turn's response, retrying on transient
    // stream disconnections. Mirrors SessionRunner.HandlerOutcome's shape — avoids the ~10
    // mutable accumulator locals (sb, toolCallsThisTurn, fileChanges, token counters, etc.)
    // that used to be threaded through the rest of ExecuteAsync after this method returns.
    private readonly record struct TurnStreamResult(
        bool Success,
        string ResponseText,
        List<string> ToolCallsThisTurn,
        List<(char Sigil, string Path)> FileChanges,
        int ToolRounds,
        List<(string ToolName, string Output)>? CapturedResults,
        long TurnInputTokens,
        long TurnOutputTokens,
        int? TurnFirstInputTokens)
    {
        internal static TurnStreamResult Failed => new(false, "", [], [], 0, null, 0, 0, null);
    }

    /// <summary>
    /// Streams one turn's response from <paramref name="ctx"/>'s active client, retrying
    /// automatically on transient mid-stream disconnections (up to <see cref="MaxStreamRetries"/>
    /// times). Owns the spinner lifecycle and the in-flight request's <see cref="CancellationTokenSource"/>
    /// entirely — nothing about it leaks into the caller. Returns <see cref="TurnStreamResult.Success"/>
    /// <see langword="false"/> on cancellation or a non-retryable/exhausted-retry failure, in which
    /// case the caller must stop processing this turn (the error has already been surfaced to the
    /// user and the trailing user message rolled back).
    /// </summary>
    private static async Task<TurnStreamResult> StreamTurnResponseAsync(
        ReplSessionContext ctx,
        string input,
        bool isStepRequest,
        bool capturePlan,
        DateTime turnStart,
        CancellationToken cancellationToken)
    {
        var sb                = new StringBuilder();
        var toolCallsThisTurn = new List<string>();
        var fileChanges        = new List<(char Sigil, string Path)>();
        var fileChangeSeen     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toolRounds        = 0;
        var inToolBatch       = false;
        var turnInputTokens   = 0L;
        var turnOutputTokens  = 0L;
        int? turnFirstInputTokens = null;
        // Captured tool outputs for inspect-step history injection (step execution only).
        List<(string ToolName, string Output)>? capturedResults = isStepRequest ? [] : null;
        Dictionary<string, string>? callIdToName = isStepRequest ? [] : null;

        var reqCts    = new CancellationTokenSource();
        ctx.ActiveCts = reqCts;
        var spinCts   = CancellationTokenSource.CreateLinkedTokenSource(reqCts.Token);
        if (!ctx.JsonMode && !isStepRequest) AnsiConsole.WriteLine();
        var spinTask  = ctx.JsonMode
            ? Task.CompletedTask
            : ReplConsole.RunSpinnerAsync(capturePlan ? "planning…" : "thinking…", spinCts.Token, turnStart);
        var spinning  = !ctx.JsonMode;

        // Cancels and awaits the spinner; caller disposes spinCts.
        async Task StopSpinnerAsync()
        {
            if (!spinning) return;
            spinning = false;
            spinCts.Cancel();
            await spinTask;
            ReplConsole.ClearSpinnerLine();
        }

        var activeClient   = isStepRequest ? ctx.StepClient : ctx.Client;
        var requestOptions = BuildRequestOptions(ctx.ChatOptions, input);
        var streamAttempt  = 0;
        while (true) // retry loop for transient streaming errors
        {
        try
        {
            await foreach (var chunk in activeClient.GetStreamingResponseAsync(
                ctx.History, requestOptions, cancellationToken: reqCts.Token))
            {
                // Providers emit a trailing usage-only chunk per underlying LLM call — a turn
                // with tool round trips produces one per round trip, so sum rather than overwrite.
                // The *first* chunk's input count is kept separately: it reflects the exact size
                // of everything sent to the model as this turn began, before this turn's own
                // tool-call round trips inflated the request further.
                foreach (var usage in chunk.Contents.OfType<UsageContent>())
                {
                    turnInputTokens  += usage.Details.InputTokenCount  ?? 0;
                    turnOutputTokens += usage.Details.OutputTokenCount ?? 0;
                    turnFirstInputTokens ??= (int?)usage.Details.InputTokenCount;
                }

                var funcCall = chunk.Contents.OfType<FunctionCallContent>().FirstOrDefault();
                if (funcCall is not null)
                {
                    if (!inToolBatch) { toolRounds++; inToolBatch = true; }
                    toolCallsThisTurn.Add(funcCall.Name);
                    TrackFileChange(funcCall.Name, funcCall.Arguments, fileChanges, fileChangeSeen, ctx.Cwd);
                    if (callIdToName is not null && funcCall.CallId is not null)
                        callIdToName[funcCall.CallId] = funcCall.Name;

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
                        spinTask = ReplConsole.RunSpinnerAsync($"{verb}…  {chain}", spinCts.Token, turnStart);
                        spinning = true;
                    }
                    continue;
                }

                var funcResult = chunk.Contents.OfType<FunctionResultContent>().FirstOrDefault();
                if (funcResult is not null && capturedResults is not null)
                {
                    var toolName = funcResult.CallId is not null &&
                                  callIdToName?.TryGetValue(funcResult.CallId, out var n) == true
                                  ? n : "tool";
                    capturedResults.Add((toolName, funcResult.Result?.ToString() ?? string.Empty));
                    continue;
                }

                var text = chunk.Text;
                if (string.IsNullOrEmpty(text)) continue;
                inToolBatch = false;
                sb.Append(text);

                // Terminal REPL never prints text live — only the spinner/tool chain is
                // shown while generating; the full response is markdown-rendered once the
                // turn completes (see below). JSON mode still streams tokens for the
                // VS Code integration's own renderer.
                if (!capturePlan && ctx.JsonMode)
                    ReplJsonBridge.Emit(new { type = "token", text });
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
            return TurnStreamResult.Failed;
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
            sb.Clear(); toolCallsThisTurn.Clear();
            fileChanges.Clear(); fileChangeSeen.Clear();
            capturedResults?.Clear(); callIdToName?.Clear();
            toolRounds = 0; inToolBatch = false;
            turnInputTokens = 0; turnOutputTokens = 0; turnFirstInputTokens = null;

            // Restart spinner for the fresh attempt.
            spinCts  = CancellationTokenSource.CreateLinkedTokenSource(reqCts.Token);
            spinTask = ctx.JsonMode
                ? Task.CompletedTask
                : ReplConsole.RunSpinnerAsync(capturePlan ? "planning…" : "thinking…", spinCts.Token, turnStart);
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

            var toolSurfaceHint = BuildLargeToolSurfaceHint(ex, ctx.GetActiveTools().Count);
            if (ctx.JsonMode)
                ReplJsonBridge.Emit(new
                {
                    type = "error",
                    text = toolSurfaceHint is null ? ex.Message : $"{ex.Message}\n{toolSurfaceHint}",
                });
            else
            {
                AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
                if (toolSurfaceHint is not null)
                    AnsiConsole.MarkupLine($"[dim]  ↪ {Markup.Escape(toolSurfaceHint)}[/]");
            }
            if (ctx.History.Count > 0 && ctx.History[^1].Role == ChatRole.User)
                ctx.History.RemoveAt(ctx.History.Count - 1);
            ctx.ExecutionQueue.Clear();
            reqCts.Dispose();
            ctx.ActiveCts = null;
            return TurnStreamResult.Failed;
        }
        } // end while (retry loop)

        reqCts.Dispose();
        ctx.ActiveCts = null;
        await StopSpinnerAsync();
        spinCts.Dispose();

        ctx.CumulativeInputTokens  += turnInputTokens;
        ctx.CumulativeOutputTokens += turnOutputTokens;
        ctx.LastActualContextTokens = turnFirstInputTokens;

        return new TurnStreamResult(
            true, sb.ToString(), toolCallsThisTurn, fileChanges, toolRounds, capturedResults,
            turnInputTokens, turnOutputTokens, turnFirstInputTokens);
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

    // Returns the number of ChatMessage entries removed (0 when no trimming was needed).
    internal static int TrimHistory(List<ChatMessage> history, int contextTokenBudget)
    {
        static int EstimateMessage(ChatMessage m) =>
            m.Contents.Sum(AgentContextCompactionFilters.EstimateContentChars) / 4;

        var total = history.Sum(EstimateMessage);
        if (total <= contextTokenBudget) return 0;

        int sysEnd  = history.Count > 0 && history[0].Role == ChatRole.System ? 1 : 0;
        int removed = 0;
        while (total > contextTokenBudget)
        {
            // Evict the oldest complete turn group (User + all following non-User
            // messages). Removing partial groups can leave orphaned FunctionCallContent
            // without a preceding User message, which is invalid for Anthropic.
            if (sysEnd >= history.Count || history[sysEnd].Role != ChatRole.User)
                break;

            int nextUserIdx = sysEnd + 1;
            while (nextUserIdx < history.Count && history[nextUserIdx].Role != ChatRole.User)
                nextUserIdx++;

            // Always keep at least one turn group.
            if (nextUserIdx >= history.Count)
                break;

            int groupSize = nextUserIdx - sysEnd;
            for (int i = 0; i < groupSize; i++)
            {
                total -= EstimateMessage(history[sysEnd]);
                history.RemoveAt(sysEnd);
                removed++;
            }
        }
        return removed;
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

}
