using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Cli.Display;
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
    private const int MaxStreamRetries = 2;

    // Write-class tools whose presence confirms the agent actually mutated state.
    private static readonly HashSet<string> MutationTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "patch_file", "create_directory", "delete_file",
        "move_file", "copy_file", "set_permissions", "shell_run",
        "git_commit", "git_add", "git_rebase",
    };

    private static readonly Regex FirstPersonMutationRegex = new(
        @"\bI(?:'ve| have| just)?\s+(updated|created|fixed|modified|patched|deleted|saved|written)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

                await ExecuteAsync(
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

                await ExecuteAsync(ctx, input, isStepRequest: false, capturePlan: false, activeStep: null, cancellationToken);
                _ = ReplTurn.SaveSnapshotAsync(ctx);
                continue;
            }

            if (raw.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("quit", StringComparison.OrdinalIgnoreCase))
                break;

            await ExecuteAsync(
                ctx, raw,
                isStepRequest: false, capturePlan: false, activeStep: null,
                cancellationToken);
            _ = ReplTurn.SaveSnapshotAsync(ctx);
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
        ctx.Emitter.SetTurn(ctx.TurnIndex);
        await ctx.Emitter.EmitAsync(EventTypes.UserInput, turn: ctx.TurnIndex, payload: new { content = input });
        ctx.History.Add(new ChatMessage(ChatRole.User, input));
        await ctx.Emitter.EmitAsync(EventTypes.TurnStart, turn: ctx.TurnIndex, payload: new { is_step = isStepRequest, is_correction = isCorrectionTurn });

        if (!isStepRequest)
            _ = ReplTurn.SaveSnapshotAsync(ctx);

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
            : ReplTurn.RunSpinnerAsync(capturePlan ? "planning…" : "thinking…", spinCts.Token, turnStart);
        var spinning  = !ctx.JsonMode;

        async Task StopSpinnerAsync()
        {
            if (!spinning) return;
            spinning = false;
            spinCts.Cancel();
            await spinTask;
            ReplTurn.ClearSpinnerLine();
        }

        var activeClient  = isStepRequest ? ctx.StepClient : ctx.Client;
        var streamAttempt = 0;
        while (true)
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
                        var args = funcCall.Arguments is { Count: > 0 }
                            ? (object)funcCall.Arguments
                            : null;
                        ReplJsonBridge.Emit(new { type = "tool_call", name = funcCall.Name, args });
                    }
                    else
                    {
                        if (textStarted && !Console.IsOutputRedirected)
                        {
                            AnsiConsole.WriteLine();
                            totalLinesAdvanced++;
                            charsOnLine = 0;
                        }

                        var chain = toolCallsThisTurn.Count <= 4
                            ? string.Join(" → ", toolCallsThisTurn)
                            : string.Join(" → ", toolCallsThisTurn.TakeLast(4)) +
                              $" (+{toolCallsThisTurn.Count - 4})";
                        spinCts.Cancel();
                        await spinTask;
                        spinCts.Dispose();
                        spinCts  = CancellationTokenSource.CreateLinkedTokenSource(reqCts.Token);
                        spinTask = ReplTurn.RunSpinnerAsync($"working…  {chain}", spinCts.Token, turnStart);
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
                                ReplTurn.ClearSpinnerLine();
                            AnsiConsole.WriteLine();
                            if (!Console.IsOutputRedirected)
                                Console.Write("\x1b7"); // save cursor at start of text area
                            totalLinesAdvanced = 0;
                            charsOnLine        = 0;
                        }
                        else if (spinning)
                        {
                            await StopSpinnerAsync();
                            if (!Console.IsOutputRedirected)
                            {
                                var th = Math.Max(Console.WindowHeight, 1);
                                if (totalLinesAdvanced < th - 1)
                                {
                                    Console.Write("\x1b8\x1b[J"); // restore cursor + clear
                                    Console.Write("\x1b7");       // re-save for next batch
                                }
                                else
                                {
                                    Console.WriteLine();          // scrolled: continue below
                                    Console.Write("\x1b7");
                                }
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
            break;
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

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, streamAttempt)));

            sb.Clear(); toolCallsThisTurn.Clear(); toolCallDetails.Clear();
            fileChanges.Clear(); fileChangeSeen.Clear();
            toolRounds = 0; inToolBatch = false; textStarted = false;
            totalLinesAdvanced = 0; charsOnLine = 0;

            spinCts  = CancellationTokenSource.CreateLinkedTokenSource(reqCts.Token);
            spinTask = ctx.JsonMode
                ? Task.CompletedTask
                : ReplTurn.RunSpinnerAsync(capturePlan ? "planning…" : "thinking…", spinCts.Token, turnStart);
            spinning = !ctx.JsonMode;
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
        }

        reqCts.Dispose();
        ctx.ActiveCts = null;
        await StopSpinnerAsync();
        spinCts.Dispose();

        var responseText = sb.ToString();

        if (!capturePlan && responseText.Length > 0 && !ctx.JsonMode)
        {
            if (!textStarted)
            {
                if (!Console.IsOutputRedirected)
                    ReplTurn.ClearSpinnerLine();
                AnsiConsole.WriteLine();
                AnsiConsole.Write(MarkdownRenderer.Render(responseText));
            }
            else
            {
                if (!Console.IsOutputRedirected)
                {
                    var termHeight = Math.Max(Console.WindowHeight, 1);
                    if (totalLinesAdvanced < termHeight - 1)
                        Console.Write("\x1b8\x1b[J"); // restore saved cursor + clear
                    else
                        Console.WriteLine();           // scrolled: render below raw text
                }
                AnsiConsole.Write(MarkdownRenderer.Render(responseText));
            }
        }
        if (!ctx.JsonMode) AnsiConsole.WriteLine();
        if (responseText.Length > 0)
            ctx.History.Add(new ChatMessage(ChatRole.Assistant, responseText));
        else if (!capturePlan)
        {
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
            ReplTurn.HandlePlanCapture(ctx, responseText);

        bool stepPassed = true;
        if (isStepRequest && activeStep is not null)
            stepPassed = await ReplTurn.HandleStepResult(ctx, activeStep, stepTotal, toolCallsThisTurn,
                hitIterationCap: toolRounds >= ReplTurn.StepIterationLimit, responseText, cancellationToken);

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

        if (!ctx.JsonMode && !isStepRequest && !capturePlan && responseText.Length > 0 && !Console.IsOutputRedirected)
        {
            var elapsed    = DateTime.UtcNow - turnStart;
            var elapsedStr = elapsed.TotalSeconds >= 1 ? $" · {(int)elapsed.TotalSeconds}s" : string.Empty;
            var toolStr    = toolCallsThisTurn.Count > 0
                ? $" · {toolCallsThisTurn.Count} tool{(toolCallsThisTurn.Count == 1 ? "" : "s")}"
                : string.Empty;
            AnsiConsole.MarkupLine(
                $"[dim]  {ctx.TurnIndex + 1} · ~{postEst:N0} tok{toolStr}{elapsedStr}[/]");
            foreach (var (sigil, path) in fileChanges)
            {
                var sigilColor = sigil == 'D' ? "red" : sigil == 'A' ? "green" : "yellow";
                AnsiConsole.MarkupLine($"  [{sigilColor}]{sigil}[/] [dim]{Markup.Escape(path)}[/]");
            }
        }

        if (!ctx.ContextWarningShown && !isStepRequest && !capturePlan && responseText.Length > 0)
        {
            var pct = (double)postEst / ReplTurn.ContextTokenBudget;
            if (pct >= 0.75)
            {
                ctx.ContextWarningShown = true;
                await ctx.Emitter.EmitAsync(EventTypes.ContextWarning, turn: ctx.TurnIndex, payload: new
                {
                    estimated_tokens = postEst,
                    budget           = ReplTurn.ContextTokenBudget,
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

        if (ReplTurn.TrimHistory(ctx.History))
        {
            if (!ctx.JsonMode)
                AnsiConsole.MarkupLine("[dim]  (old messages trimmed to fit context window)[/]");
        }

        if (!ctx.JsonMode && ctx.Verbose)
            AnsiConsole.MarkupLine(
                $"[dim]  tokens (est.): {postEst:N0} / {ReplTurn.ContextTokenBudget:N0}  rounds: {toolRounds}  tool calls: {toolCallsThisTurn.Count}[/]");

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

    // -------------------------------------------------------------------------
    // Private utilities (subset of ReplTurn private helpers)
    // -------------------------------------------------------------------------

    private static bool IsTransientStreamError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException) return false;
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

    private static bool ContainsMutationClaim(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!FirstPersonMutationRegex.IsMatch(text)) return false;
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
