using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Cli.Display;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Repl;

internal static class ReplTurn
{
    internal static readonly string[] SpinnerFrames = OperatingSystem.IsWindows()
        ? ["-", "\\", "|", "/"]
        : ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    internal const int ContextTokenBudget = 80_000;
    internal const int StepIterationLimit = 5;

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
                continue;
            }

            var turnLabel = (ctx.TurnIndex + 1).ToString();
            AnsiConsole.Markup(ctx.SafeMode
                ? $"[dim][[safe]] {turnLabel}[/][bold cyan]>[/] "
                : $"[dim]{turnLabel}[/][bold cyan]>[/] ");

            string? raw;
            try   { raw = ctx.LineReader.ReadLine(); }
            catch (OperationCanceledException) { break; }

            if (raw is null) break;
            raw = raw.Trim();
            if (string.IsNullOrEmpty(raw)) continue;

            if (raw.StartsWith('/'))
            {
                var parts   = raw.Split(' ', 2, StringSplitOptions.TrimEntries);
                var command = parts[0].ToLowerInvariant();
                var arg     = parts.Length > 1 ? parts[1] : string.Empty;

                var result = await ReplCommands.HandleAsync(ctx, command, arg, cancellationToken);
                AnsiConsole.WriteLine();

                if (result.Outcome == CommandOutcome.Exit)     break;
                if (result.Outcome == CommandOutcome.Continue) continue;

                await ExecuteAsync(
                    ctx,
                    result.InputOverride!,
                    isStepRequest: false,
                    capturePlan:   result.CapturePlan,
                    activeStep:    null,
                    cancellationToken);
                continue;
            }

            if (raw.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("quit", StringComparison.OrdinalIgnoreCase))
                break;

            await ExecuteAsync(
                ctx, raw,
                isStepRequest: false, capturePlan: false, activeStep: null,
                cancellationToken);
        }
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
        await ctx.Emitter.EmitAsync("user_input", turn: ctx.TurnIndex, payload: new { content = input });
        ctx.History.Add(new ChatMessage(ChatRole.User, input));

        var sb                = new StringBuilder();
        var toolCallsThisTurn = new List<string>();
        var toolRounds        = 0;
        var inToolBatch       = false;
        var textStarted       = false;

        var reqCts    = new CancellationTokenSource();
        ctx.ActiveCts = reqCts;
        var spinCts   = CancellationTokenSource.CreateLinkedTokenSource(reqCts.Token);
        var spinTask  = RunSpinnerAsync(capturePlan ? "planning…" : "thinking…", spinCts.Token);
        var spinning  = true;

        // Cancels and awaits the spinner; caller disposes spinCts.
        async Task StopSpinnerAsync()
        {
            if (!spinning) return;
            spinning = false;
            spinCts.Cancel();
            await spinTask;
            ClearSpinnerLine();
        }

        var activeClient = isStepRequest ? ctx.StepClient : ctx.Client;
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

                    // Update spinner label to show the accumulating tool chain live.
                    var chain = toolCallsThisTurn.Count <= 4
                        ? string.Join(" → ", toolCallsThisTurn)
                        : string.Join(" → ", toolCallsThisTurn.TakeLast(4)) +
                          $" (+{toolCallsThisTurn.Count - 4})";
                    spinCts.Dispose();
                    spinCts  = CancellationTokenSource.CreateLinkedTokenSource(reqCts.Token);
                    spinTask = RunSpinnerAsync($"conjuring…  {chain}", spinCts.Token);
                    spinning = true;
                    continue;
                }

                var text = chunk.Text;
                if (string.IsNullOrEmpty(text)) continue;
                inToolBatch = false;
                sb.Append(text);

                if (!capturePlan)
                {
                    if (!textStarted)
                    {
                        textStarted = true;
                        await StopSpinnerAsync();
                        // Print compact tool-call chain before the response starts.
                        if (toolCallsThisTurn.Count > 0 && !Console.IsOutputRedirected)
                            AnsiConsole.MarkupLine(
                                $"  [dim]⚙  {Markup.Escape(string.Join(" → ", toolCallsThisTurn))}[/]");
                    }
                    else if (spinning)
                    {
                        await StopSpinnerAsync();
                    }
                    if (!Console.IsOutputRedirected)
                    {
                        var approxTokens = (sb.Length + 3) / 4;
                        Console.Write($"\r\x1b[2m receiving… {approxTokens} tokens\x1b[0m  ");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            await StopSpinnerAsync();
            spinCts.Dispose();
            AnsiConsole.MarkupLine("[dim](cancelled)[/]");
            if (ctx.History.Count > 0 && ctx.History[^1].Role == ChatRole.User)
                ctx.History.RemoveAt(ctx.History.Count - 1);
            ctx.ExecutionQueue.Clear();
            AnsiConsole.WriteLine();
            reqCts.Dispose();
            ctx.ActiveCts = null;
            return false;
        }
        catch (Exception ex)
        {
            await StopSpinnerAsync();
            spinCts.Dispose();
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            if (ctx.History.Count > 0 && ctx.History[^1].Role == ChatRole.User)
                ctx.History.RemoveAt(ctx.History.Count - 1);
            ctx.ExecutionQueue.Clear();
            reqCts.Dispose();
            ctx.ActiveCts = null;
            return false;
        }

        reqCts.Dispose();
        ctx.ActiveCts = null;
        await StopSpinnerAsync();
        spinCts.Dispose();

        var responseText = sb.ToString();

        if (!capturePlan && responseText.Length > 0)
        {
            if (!Console.IsOutputRedirected)
                ClearSpinnerLine();
            AnsiConsole.MarkupLine("[dim]assistant:[/]");
            AnsiConsole.Write(MarkdownRenderer.Render(responseText));
        }
        AnsiConsole.WriteLine();
        if (responseText.Length > 0)
            ctx.History.Add(new ChatMessage(ChatRole.Assistant, responseText));

        if (capturePlan && responseText.Length > 0)
            HandlePlanCapture(ctx, responseText);

        bool stepPassed = true;
        if (isStepRequest && activeStep is not null)
            stepPassed = HandleStepResult(ctx, activeStep, stepTotal, toolCallsThisTurn, hitIterationCap: toolRounds >= StepIterationLimit);

        // Free-form turns: if the response claims a mutation but no write tool was called,
        // auto-inject a correction so the agent is required to actually call the tool.
        // On the correction turn itself fall back to a warning to avoid infinite recursion.
        if (!isStepRequest && !capturePlan && responseText.Length > 0 &&
            !toolCallsThisTurn.Any(t => MutationTools.Contains(t)) &&
            ContainsMutationClaim(responseText))
        {
            if (!isCorrectionTurn)
            {
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
                AnsiConsole.MarkupLine(
                    "[yellow]  ⚠ No write tool called after correction — verify the agent did not fabricate this result.[/]");
            }
        }

        var postEst = ctx.EstimateTokens();
        if (ctx.PrevTurnTokenEstimate > 0)
            ctx.TurnTokenDeltas.Add(postEst - ctx.PrevTurnTokenEstimate);
        ctx.PrevTurnTokenEstimate = postEst;

        // Compact status line after each free-form response.
        if (!isStepRequest && !capturePlan && responseText.Length > 0 && !Console.IsOutputRedirected)
        {
            var toolStr = toolCallsThisTurn.Count > 0
                ? $" · {toolCallsThisTurn.Count} tool{(toolCallsThisTurn.Count == 1 ? "" : "s")}"
                : string.Empty;
            AnsiConsole.MarkupLine(
                $"[dim]  ── turn {ctx.TurnIndex + 1} · ~{postEst:N0} tok{toolStr}[/]");
        }

        if (TrimHistory(ctx.History))
            AnsiConsole.MarkupLine("[dim]  (old messages trimmed to fit context window)[/]");

        if (ctx.Verbose)
            AnsiConsole.MarkupLine(
                $"[dim]  tokens (est.): {postEst:N0} / {ContextTokenBudget:N0}  rounds: {toolRounds}  tool calls: {toolCallsThisTurn.Count}[/]");

        foreach (var tool in toolCallsThisTurn)
            await ctx.Emitter.EmitAsync("tool_call", turn: ctx.TurnIndex, payload: new { tool_name = tool });
        await ctx.Emitter.EmitAsync("assistant_response", turn: ctx.TurnIndex, payload: new { content = responseText });

        if (ctx.PendingSave && responseText.Length > 0)
        {
            UserConfigStore.Save(ctx.UserCfg!);
            AnsiConsole.MarkupLine($"[dim]Settings saved to[/] [bold]{Markup.Escape(UserConfigStore.ConfigPath)}[/]");
            AnsiConsole.MarkupLine($"[dim]API key stored in[/] [bold]{Markup.Escape(ctx.KeyStore.StoreName)}[/]");
            ctx.PendingSave = false;
        }

        ctx.TurnIndex++;
        return stepPassed;
    }

    internal static void HandlePlanCapture(ReplSessionContext ctx, string responseText)
    {
        if (TryParsePlan(responseText, out var steps) && steps.Length > 0)
        {
            ctx.CurrentPlan = steps;
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
        else
        {
            AnsiConsole.MarkupLine("[yellow]⚠ Could not parse plan JSON. Raw response:[/]");
            Console.WriteLine(responseText);
            AnsiConsole.MarkupLine("[dim]Try /plan again.[/]");
            AnsiConsole.WriteLine();
        }
    }

    internal static bool HandleStepResult(
        ReplSessionContext ctx, PlanStep activeStep, int total, List<string> toolCallsThisTurn, bool hitIterationCap)
    {
        var passed    = VerifyStep(activeStep, toolCallsThisTurn, ctx.Cwd);
        var stepsLeft = ctx.ExecutionQueue.Count;
        if (passed)
        {
            var zeroCallSkip  = activeStep.Tool is not null && toolCallsThisTurn.Count == 0;
            var inspectSkip   = activeStep.Tool is not null && toolCallsThisTurn.Count > 0 &&
                                toolCallsThisTurn.All(t => InspectTools.Contains(t));
            var skipped       = zeroCallSkip || inspectSkip;
            var icon          = skipped ? "↷" : "✓";
            var label         = skipped ? "skipped" : "complete";
            AnsiConsole.MarkupLine(stepsLeft > 0
                ? $"[dim]  {icon} Step {activeStep.Step} {label}.  {stepsLeft} step{(stepsLeft == 1 ? "" : "s")} remaining.[/]"
                : $"[dim]  {icon} Step {activeStep.Step} {label}.  Plan finished.[/]");
            if (hitIterationCap)
                AnsiConsole.MarkupLine(
                    $"[dim]  ↯ Step {activeStep.Step} reached the {StepIterationLimit}-round limit; later calls in this step may have been cut short.[/]");
            // A write-tool step with zero tool calls is suspicious: the agent may have fabricated output.
            if (zeroCallSkip && activeStep.Tool is not null && !InspectTools.Contains(activeStep.Tool))
                AnsiConsole.MarkupLine(
                    $"[yellow]  ⚠ Step {activeStep.Step}: '{Markup.Escape(activeStep.Tool)}' was not called — verify the agent did not fabricate this result.[/]");
        }
        else
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
            ctx.HaltedAt = (activeStep, total);
            ctx.HaltedRemaining.Clear();
            foreach (var item in ctx.ExecutionQueue) ctx.HaltedRemaining.Enqueue(item);
            ctx.HaltedToolCalls = [.. toolCallsThisTurn];
            ctx.ExecutionQueue.Clear();
            AnsiConsole.MarkupLine("[yellow]  Plan halted. Run /recover to let the agent diagnose and retry, or /resume to retry directly.[/]");
        }
        AnsiConsole.WriteLine();
        return passed;
    }

    internal static async Task ExtractMemoriesOnExitAsync(ReplSessionContext ctx)
    {
        if (ctx.TurnIndex == 0 || ctx.LastExtractedTurnIndex == ctx.TurnIndex) return;
        try
        {
            AnsiConsole.Markup("[dim]saving memory…[/]");
            var mc = ctx.Factory.Create(ctx.ModelConfig);
            using var _ = mc as IDisposable;
            var existing = await ctx.MemoryStore.LoadAllAsync(ctx.Cwd);
            var (saved, parseFailed) = await new MemoryExtractor(mc).ExtractAsync([.. ctx.History], existing);
            Console.Write($"\r{new string(' ', 30)}\r");
            foreach (var m in saved) await ctx.MemoryStore.SaveAsync(m, ctx.Cwd);
            if (parseFailed)
                AnsiConsole.MarkupLine("[dim](memory extraction returned unparseable output)[/]");
            else if (saved.Count > 0)
                AnsiConsole.MarkupLine(
                    $"[dim]Memory: {saved.Count} entr{(saved.Count == 1 ? "y" : "ies")} saved.[/]");
        }
        catch
        {
            Console.Write($"\r{new string(' ', 30)}\r");
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
            // Remove one user message then the immediately following assistant message.
            // Consecutive user turns (e.g. injected step summaries) are removed one per
            // iteration; the assistant check below safely no-ops when history[start] is
            // still another user message after the removal.
            if (history[start].Role == ChatRole.User)
            {
                total -= Estimate(history[start]);
                history.RemoveAt(start);
            }
            if (start < history.Count && history[start].Role == ChatRole.Assistant)
            {
                total -= Estimate(history[start]);
                history.RemoveAt(start);
            }
        }
        return true;
    }

    internal static string BuildStepMessage(PlanStep step, int total)
    {
        var sb = new StringBuilder();
        sb.Append($"Execute step {step.Step} of {total}: {step.Description}");
        if (step.Tool    is not null) sb.Append($"\nExpected tool: {step.Tool}");
        if (step.Creates is not null) sb.Append($"\nExpected artifact: {step.Creates}");
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
        "git_commit", "git_add",
    };

    private static readonly string[] MutationClaimVerbs =
        ["updated", "created", "fixed", "modified", "patched", "deleted", "saved", "written"];

    private static bool ContainsMutationClaim(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lower = text.ToLowerInvariant();
        if (!MutationClaimVerbs.Any(v => lower.Contains(v))) return false;
        // Require a file-like reference to reduce false positives on conversational text.
        return lower.Contains('/') ||
               lower.Contains(".md") || lower.Contains(".cs") || lower.Contains(".py") ||
               lower.Contains(".js") || lower.Contains(".ts") || lower.Contains(".json") ||
               lower.Contains(".xml") || lower.Contains(".yaml") || lower.Contains(".txt") ||
               lower.Contains(".drawio") || lower.Contains(".sh") || lower.Contains(".toml");
    }

    internal static bool VerifyStep(PlanStep step, List<string> toolCalls, string cwd)
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
        return toolOk && fileOk;
    }

    internal static bool TryParsePlan(string text, out PlanStep[] steps)
    {
        steps = [];
        var trimmed  = text.Trim();
        var startIdx = trimmed.IndexOf('[');
        var endIdx   = trimmed.LastIndexOf(']');
        if (startIdx < 0 || endIdx <= startIdx) return false;
        var json = trimmed[startIdx..(endIdx + 1)];
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            steps = JsonSerializer.Deserialize<PlanStep[]>(json, opts) ?? [];
            return steps.Length > 0;
        }
        catch { return false; }
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

    internal static async Task RunSpinnerAsync(string label, CancellationToken ct)
    {
        var i = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Console.Write($"\r\x1b[2m{SpinnerFrames[i % SpinnerFrames.Length]} {label}\x1b[0m  ");
                i++;
                await Task.Delay(80, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal static void ClearSpinnerLine()
    {
        var width = Console.IsOutputRedirected ? 80 : Math.Max(Console.WindowWidth - 1, 80);
        Console.Write($"\r{new string(' ', width)}\r");
    }
}
