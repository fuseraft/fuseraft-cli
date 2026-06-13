using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Cli.Display;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands.Repl;

internal static class ReplCommands
{
    internal static async Task<CommandResult> HandleAsync(
        ReplSessionContext ctx, string command, string arg, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "/exit":       return CommandResult.Exit;
            case "/help":       PrintHelp(ctx.JsonMode); return CommandResult.Continue;
            case "/clear":      return await CmdClearAsync(ctx);
            case "/system":     return CmdSystem(ctx, arg);
            case "/tools":      return await CmdToolsAsync(ctx, arg);
            case "/paste":      return CmdPaste(ctx.JsonMode);
            case "/save":       return await CmdSaveAsync(ctx, arg);
            case "/history":    CmdHistory(ctx); return CommandResult.Continue;
            case "/context":    await CmdContextAsync(ctx); return CommandResult.Continue;
            case "/provider":   return await CmdProviderAsync(ctx, arg);
            case "/plan":       return await CmdPlanAsync(ctx, arg);
            case "/execute":    return await CmdExecuteAsync(ctx);
            case "/resume":     return CmdResume(ctx);
            case "/recover":    return CmdRecover(ctx);
            case "/events":     await CmdEventsAsync(ctx, arg); return CommandResult.Continue;
            case "/safe-mode":    return await CmdSafeModeAsync(ctx, arg);
            case "/adversarial":  return CmdAdversarial(ctx, arg);
            case "/assist":       return await CmdAssistAsync(ctx, cancellationToken);
            case "/memory":     return await CmdMemoryAsync(ctx, arg, cancellationToken);
            case "/max-tokens": return CmdMaxTokens(ctx, arg);
            case "/compact":    return await CmdCompactAsync(ctx, arg, cancellationToken);
            case "/explore":    return await CmdExploreAsync(ctx, arg, cancellationToken);
            case "/locate":     return await CmdLocateAsync(ctx, arg, cancellationToken);
            case "/sessions":      await CmdSessionsAsync(ctx.JsonMode, cancellationToken); return CommandResult.Continue;
            case "/fork":          return await CmdForkAsync(ctx, arg, cancellationToken);
            case "/switch":        return await CmdSwitchAsync(ctx, arg, cancellationToken);
            case "/conversation":  CmdConversation(ctx); return CommandResult.Continue;
            case "/rewind":        return await CmdRewindAsync(ctx, arg, cancellationToken);
            case "/model":         return await CmdModelAsync(ctx, arg);
            case "/reasoning":     return await CmdReasoningAsync(ctx, arg);
            case "/retry":         return CmdRetry(ctx);
            case "/last":          CmdLast(ctx); return CommandResult.Continue;
            case "/snapshot":      await CmdSnapshotAsync(ctx); return CommandResult.Continue;
            case "/run":           return await CmdRunAsync(ctx, arg, cancellationToken);
            default:
                AnsiConsole.MarkupLine(
                    $"[yellow]Unknown command:[/] {Markup.Escape(command)}  [dim](type /help for commands)[/]");
                return CommandResult.Continue;
        }
    }

    // -------------------------------------------------------------------------
    // Command handlers
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdClearAsync(ReplSessionContext ctx)
    {
        var sys = ctx.History.FirstOrDefault(m => m.Role == ChatRole.System);
        ctx.History.Clear();
        if (sys is not null) ctx.History.Add(sys);
        ctx.TurnIndex              = 0;
        ctx.PrevTurnTokenEstimate  = 0;
        ctx.TurnTokenDeltas.Clear();
        ctx.ContextWarningShown    = false;
        ctx.ResetPlanState();
        AnsiConsole.MarkupLine("[dim]History cleared.[/]");
        await ctx.Emitter.EmitAsync("command", payload: new { command = "/clear" });
        return CommandResult.Continue;
    }

    private static CommandResult CmdSystem(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            var current = ctx.History.FirstOrDefault(m => m.Role == ChatRole.System);
            AnsiConsole.MarkupLine(current is not null
                ? $"[dim]System prompt:[/] {Markup.Escape(current.Text ?? "(empty)")}"
                : "[dim]No system prompt set.[/]");
        }
        else
        {
            var updated = arg + $"\n\nThe current working directory is: {ctx.Cwd}.";
            ctx.History.RemoveAll(m => m.Role == ChatRole.System);
            ctx.History.Insert(0, new ChatMessage(ChatRole.System, updated));
            AnsiConsole.MarkupLine("[dim]System prompt updated.[/]");
            _ = ctx.Emitter.EmitAsync("command", payload: new { command = "/system", prompt = arg });
        }
        return CommandResult.Continue;
    }

    private static async Task<CommandResult> CmdToolsAsync(ReplSessionContext ctx, string arg)
    {
        if (ctx.ToolsByCategory.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No tools enabled (--no-tools was set).[/]");
            return CommandResult.Continue;
        }

        if (string.IsNullOrEmpty(arg))
        {
            var activeCnt = ctx.GetActiveTools().Count;
            AnsiConsole.MarkupLine(
                $"[dim]{activeCnt} tools active " +
                $"({ctx.ToolsByCategory.Count - ctx.DisabledCategories.Count}/{ctx.ToolsByCategory.Count} categories):[/]");
            foreach (var (catName, funcs) in ctx.ToolsByCategory)
            {
                var off = ctx.DisabledCategories.Contains(catName);
                AnsiConsole.MarkupLine(off
                    ? $"  [dim]  [[{Markup.Escape(catName)}]] (disabled)[/]"
                    : $"  [dim]  [[{Markup.Escape(catName)}]][/]");
                if (!off)
                    foreach (var t in funcs)
                        AnsiConsole.MarkupLine($"  [dim]    ·[/] {Markup.Escape(t.Name)}");
            }
            return CommandResult.Continue;
        }

        var sub  = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
        var verb = sub[0].ToLowerInvariant();
        var cat  = sub.Length > 1 ? sub[1] : string.Empty;

        if ((verb == "disable" || verb == "enable") && !string.IsNullOrEmpty(cat))
        {
            var match = ctx.ToolsByCategory.Keys.FirstOrDefault(
                k => k.Equals(cat, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                AnsiConsole.MarkupLine($"[yellow]Unknown category:[/] {Markup.Escape(cat)}");
                AnsiConsole.MarkupLine($"[dim]Categories: {string.Join(", ", ctx.ToolsByCategory.Keys)}[/]");
            }
            else if (verb == "disable")
            {
                ctx.DisabledCategories.Add(match);
                // Rebuild ChatOptions only — FunctionInvokingChatClient reads the tool list
                // from ChatOptions at call time, so Client/StepClient don't need rebuilding.
                ctx.ChatOptions = ctx.BuildChatOptions();
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(match)} tools disabled.[/]");
                await ctx.Emitter.EmitAsync("command", payload: new { command = "/tools disable", category = match });
            }
            else
            {
                ctx.DisabledCategories.Remove(match);
                ctx.ChatOptions = ctx.BuildChatOptions();
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(match)} tools enabled.[/]");
                await ctx.Emitter.EmitAsync("command", payload: new { command = "/tools enable", category = match });
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown /tools subcommand:[/] {Markup.Escape(arg)}");
            AnsiConsole.MarkupLine("[dim]Usage: /tools                    — list tools by category[/]");
            AnsiConsole.MarkupLine("[dim]       /tools disable <category>  — disable a tool category[/]");
            AnsiConsole.MarkupLine("[dim]       /tools enable <category>   — enable a tool category[/]");
        }
        return CommandResult.Continue;
    }

    private static CommandResult CmdPaste(bool jsonMode)
    {
        if (jsonMode)
        {
            // Paste mode reads raw stdin lines which would corrupt the JSONL bridge.
            // The VS Code panel textarea already supports Shift+Enter for multi-line input.
            Console.WriteLine("Paste mode is not available in the VS Code panel.\n\nUse **Shift+Enter** in the input box to enter multi-line messages.");
            return CommandResult.Continue;
        }

        AnsiConsole.MarkupLine("[dim]Paste your content below. Type[/] [bold].done[/] [dim]on its own line (or press Ctrl+D) when done.[/]");
        var lines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine();
            if (line is null || line == ".done") break;
            lines.Add(line);
        }
        if (lines.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Nothing pasted.[/]");
            AnsiConsole.WriteLine();
            return CommandResult.Continue;
        }
        return CommandResult.Send(string.Join('\n', lines));
    }

    private static async Task<CommandResult> CmdSaveAsync(ReplSessionContext ctx, string arg)
    {
        var path = string.IsNullOrWhiteSpace(arg)
            ? Path.Combine(ctx.Cwd, $"repl-{ctx.SessionId}.md")
            : arg;
        SaveTranscript(ctx.History, ctx.ModelId, path);
        AnsiConsole.MarkupLine($"[dim]Transcript saved to[/] {Markup.Escape(path)}");
        await ctx.Emitter.EmitAsync("command", payload: new { command = "/save", path });
        return CommandResult.Continue;
    }

    private static void CmdHistory(ReplSessionContext ctx)
    {
        var turns = ctx.History.Where(m => m.Role != ChatRole.System).ToList();
        if (turns.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No history yet.[/]");
            return;
        }

        if (ctx.JsonMode)
        {
            Console.WriteLine($"## History ({turns.Count} message{(turns.Count == 1 ? "" : "s")})\n");
            foreach (var m in turns)
            {
                var preview = (m.Text ?? string.Empty).Replace('\n', ' ').Trim();
                if (preview.Length > 120) preview = preview[..120] + "…";
                var label = m.Role == ChatRole.User ? "**You**" : "**Assistant**";
                Console.WriteLine($"- {label}: {preview}");
            }
            return;
        }

        foreach (var m in turns)
        {
            var preview = (m.Text ?? string.Empty).Replace('\n', ' ').Trim();
            if (preview.Length > 90) preview = preview[..90] + "…";
            var label = m.Role == ChatRole.User ? "[bold cyan]user[/]" : "[dim]assistant[/]";
            AnsiConsole.MarkupLine($"  {label}: {Markup.Escape(preview)}");
        }
    }

    private static async Task CmdContextAsync(ReplSessionContext ctx)
    {
        var active   = ctx.GetActiveTools();
        var sysTok   = ctx.History.Where(m => m.Role == ChatRole.System).Sum(m => (m.Text?.Length ?? 0) / 4);
        var userTok  = ctx.History.Where(m => m.Role == ChatRole.User).Sum(m => (m.Text?.Length ?? 0) / 4);
        var asstTok  = ctx.History.Where(m => m.Role == ChatRole.Assistant).Sum(m => (m.Text?.Length ?? 0) / 4);
        var toolTok  = active.Sum(t => t.JsonSchema.GetRawText().Length / 4);
        var total    = sysTok + userTok + asstTok + toolTok;
        var pct      = (double)total / ReplTurn.ContextTokenBudget * 100;

        if (ctx.JsonMode)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Context Usage\n");
            var deltaNote = ctx.PrevCtxEstimate > 0
                ? (total - ctx.PrevCtxEstimate is var d and >= 0
                    ? $" *(+{d:N0} since last check)*"
                    : $" *({total - ctx.PrevCtxEstimate:N0} since last check)*")
                : string.Empty;
            sb.AppendLine($"**~{total:N0} / {ReplTurn.ContextTokenBudget:N0} tokens** — {pct:F1}%{deltaNote}");
            sb.AppendLine();
            sb.AppendLine($"**{ctx.TurnIndex} turn{(ctx.TurnIndex != 1 ? "s" : "")}** " +
                $"({ctx.History.Count} messages — " +
                $"system: {ctx.History.Count(m => m.Role == ChatRole.System)}, " +
                $"user: {ctx.History.Count(m => m.Role == ChatRole.User)}, " +
                $"assistant: {ctx.History.Count(m => m.Role == ChatRole.Assistant)})");
            sb.AppendLine();
            sb.AppendLine("**Breakdown**");
            if (sysTok > 0)
                sb.AppendLine($"- System prompt: {sysTok:N0} tok ({(double)sysTok / total * 100:F1}%)");
            if (active.Count > 0)
                sb.AppendLine($"- Tools ({active.Count}): {toolTok:N0} tok ({(double)toolTok / total * 100:F1}%) *(per request)*");
            sb.AppendLine($"- User messages: {userTok:N0} tok ({(double)userTok / total * 100:F1}%)");
            sb.AppendLine($"- Assistant messages: {asstTok:N0} tok ({(double)asstTok / total * 100:F1}%)");
            if (ctx.TurnTokenDeltas.Count >= 1)
            {
                var avg = (int)Math.Round(ctx.TurnTokenDeltas.Average());
                if (avg > 0)
                {
                    var proj = (ReplTurn.ContextTokenBudget - total) / avg;
                    sb.AppendLine();
                    sb.AppendLine($"*~{proj:N0} turns remaining (avg +{avg:N0} tok/turn)*");
                }
            }
            Console.Write(sb.ToString());
            ctx.PrevCtxEstimate = total;
            await ctx.Emitter.EmitAsync("command", payload: new
            {
                command = "/context",
                estimated_tokens = total,
                token_budget = ReplTurn.ContextTokenBudget,
                turns = ctx.TurnIndex,
                breakdown = new { system = sysTok, tools = toolTok, user = userTok, assistant = asstTok }
            });
            return;
        }

        var bar      = new string('█', (int)(pct / 5)).PadRight(20, '░');
        var deltaStr = ctx.PrevCtxEstimate > 0
            ? (total - ctx.PrevCtxEstimate is var d2 and >= 0
                ? $"  [dim](+{d2:N0} since last check)[/]"
                : $"  [dim]({total - ctx.PrevCtxEstimate:N0} since last check)[/]")
            : string.Empty;

        AnsiConsole.MarkupLine(
            $"  [dim]Tokens (est.):[/] [bold]{total:N0}[/] / {ReplTurn.ContextTokenBudget:N0}  " +
            $"[{(pct >= 90 ? "red" : pct >= 70 ? "yellow" : "green")}]{Markup.Escape(bar)}[/]  " +
            $"[dim]{pct:F1}%[/]{deltaStr}");
        AnsiConsole.MarkupLine(
            $"  [dim]Budget:[/]       [bold]{ReplTurn.ContextTokenBudget:N0}[/]  [dim](context window ceiling)[/]");
        AnsiConsole.MarkupLine(
            $"  [dim]Turns:[/]        [bold]{ctx.TurnIndex}[/]  " +
            $"[dim](messages: {ctx.History.Count} — " +
            $"system: {ctx.History.Count(m => m.Role == ChatRole.System)}, " +
            $"user: {ctx.History.Count(m => m.Role == ChatRole.User)}, " +
            $"assistant: {ctx.History.Count(m => m.Role == ChatRole.Assistant)})[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [dim]Breakdown:[/]");
        PrintContextRow("system prompt",  sysTok,  total);
        if (active.Count > 0)
            PrintContextRow($"tools ({active.Count})", toolTok, total, "(per req.)");
        PrintContextRow("user messages",  userTok, total);
        PrintContextRow("assistant msgs", asstTok, total);

        if (ctx.TurnTokenDeltas.Count >= 1)
        {
            var avg = (int)Math.Round(ctx.TurnTokenDeltas.Average());
            if (avg > 0)
            {
                var proj = (ReplTurn.ContextTokenBudget - total) / avg;
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"  [dim]Projected:[/]    ~{proj:N0} turns remaining  [dim](avg +{avg:N0} tok/turn)[/]");
            }
        }

        ctx.PrevCtxEstimate = total;
        await ctx.Emitter.EmitAsync("command", payload: new
        {
            command = "/context",
            estimated_tokens = total,
            token_budget = ReplTurn.ContextTokenBudget,
            turns = ctx.TurnIndex,
            breakdown = new { system = sysTok, tools = toolTok, user = userTok, assistant = asstTok }
        });
    }

    private static async Task<CommandResult> CmdProviderAsync(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            var epDisplay  = string.IsNullOrEmpty(ctx.ModelConfig.Endpoint) ? "(auto-detected)" : ctx.ModelConfig.Endpoint;
            var keyDisplay = string.IsNullOrEmpty(ctx.ModelConfig.ApiKey)
                ? "(from environment)"
                : $"••••••••  [[{Markup.Escape(ctx.KeyStore.StoreName)}]]";
            AnsiConsole.MarkupLine($"  [dim]Model:[/]    [bold]{Markup.Escape(ctx.ModelId)}[/]");
            AnsiConsole.MarkupLine($"  [dim]Endpoint:[/] {Markup.Escape(epDisplay)}");
            AnsiConsole.MarkupLine($"  [dim]API Key:[/]  {keyDisplay}");
            AnsiConsole.MarkupLine($"  [dim]Config:[/]   {Markup.Escape(UserConfigStore.ConfigPath)}");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]/provider setup[/] [dim]to reconfigure.[/]");
            return CommandResult.Continue;
        }

        if (!arg.Equals("setup", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown /provider subcommand:[/] {Markup.Escape(arg)}");
            AnsiConsole.MarkupLine("[dim]Usage: /provider          — show current settings[/]");
            AnsiConsole.MarkupLine("[dim]       /provider setup     — reconfigure provider, model, and API key[/]");
            return CommandResult.Continue;
        }

        if (ctx.JsonMode)
        {
            Console.WriteLine("Provider setup requires an interactive terminal and is not available in the VS Code panel.\n\nRun **`fuseraft repl`** in a terminal to reconfigure your provider, model, and API key.");
            return CommandResult.Continue;
        }

        AnsiConsole.WriteLine();
        var (newCfg, newKey) = ReplFactory.RunSetupWizard(ctx.ModelId, ctx.UserCfg);
        if (newCfg is null || newKey is null) return CommandResult.Continue;

        await ctx.KeyStore.StoreAsync(newKey);
        newCfg.ApiKey    = newKey;
        ctx.UserCfg      = newCfg;
        ctx.ModelId      = newCfg.ModelId;
        ctx.ModelConfig  = ReplFactory.BuildModelConfig(ctx.ModelId, ctx.UserCfg);
        try
        {
            var hasTools       = ctx.GetActiveTools().Count > 0;
            ctx.Client         = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools);
            ctx.StepClient     = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools, ReplTurn.StepIterationLimit);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Could not create chat client:[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        var sys = ctx.History.FirstOrDefault(m => m.Role == ChatRole.System);
        ctx.History.Clear();
        if (sys is not null) ctx.History.Add(sys);
        ctx.TurnIndex    = 0;
        ctx.PendingSave  = false;
        UserConfigStore.Save(ctx.UserCfg);
        AnsiConsole.MarkupLine($"[dim]Settings saved to[/] [bold]{Markup.Escape(UserConfigStore.ConfigPath)}[/]");
        AnsiConsole.MarkupLine($"[dim]API key stored in[/] [bold]{Markup.Escape(ctx.KeyStore.StoreName)}[/]");
        AnsiConsole.MarkupLine($"[dim]Model:[/] [bold]{Markup.Escape(ctx.ModelId)}[/]  [dim](history cleared)[/]");
        await ctx.Emitter.EmitAsync("command", payload: new { command = "/provider setup", model = ctx.ModelId });
        return CommandResult.Continue;
    }

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
            $"search_files, read_file, patch_file, shell_run, git_add, git_commit). " +
            $"Optionally include \"creates\" (path of a file or directory you will create, relative to " +
            $"the working directory). " +
            $"Focus on intentful actions only — no defensive steps like verifying CWD or reading files back." +
            $"\n\nTask: {arg}";

        await ctx.Emitter.EmitAsync("command", payload: new { command = "/plan", task = arg });
        return CommandResult.Send(planPrompt, capturePlan: true);
    }

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
        await ctx.Emitter.EmitAsync("command", payload: new { command = "/execute", steps = total });
        return CommandResult.Continue;
    }

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

    private static CommandResult CmdMaxTokens(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            AnsiConsole.MarkupLine(ctx.MaxOutputTokens > 0
                ? $"[dim]Max output tokens:[/] [bold]{ctx.MaxOutputTokens:N0}[/]"
                : "[dim]Max output tokens:[/] provider default");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]/max-tokens <n>[/] [dim]to set, or[/] [bold]/max-tokens reset[/] [dim]to restore the provider default.[/]");
            return CommandResult.Continue;
        }

        if (arg.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            ctx.MaxOutputTokens = 0;
            ctx.ChatOptions = ctx.BuildChatOptions();
            AnsiConsole.MarkupLine("[dim]Max output tokens reset to provider default.[/]");
            return CommandResult.Continue;
        }

        if (!int.TryParse(arg, out var n) || n <= 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Invalid value:[/] {Markup.Escape(arg)}  [dim](must be a positive integer)[/]");
            return CommandResult.Continue;
        }

        ctx.MaxOutputTokens = n;
        ctx.ChatOptions = ctx.BuildChatOptions();
        AnsiConsole.MarkupLine($"[dim]Max output tokens set to[/] [bold]{n:N0}[/][dim].[/]");
        return CommandResult.Continue;
    }

    private static async Task CmdEventsAsync(ReplSessionContext ctx, string arg)
    {
        if (!File.Exists(ctx.EventsPath))
        {
            AnsiConsole.MarkupLine($"[dim]No events file found at[/] {Markup.Escape(ctx.EventsPath)}");
            return;
        }

        if (!string.IsNullOrEmpty(arg) && !arg.Equals("stats", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown /events subcommand:[/] {Markup.Escape(arg)}");
            AnsiConsole.MarkupLine("[dim]Usage: /events        — show session event stats[/]");
            AnsiConsole.MarkupLine("[dim]       /events stats  — same[/]");
            return;
        }

        var lines       = await File.ReadAllLinesAsync(ctx.EventsPath);
        var turnSet     = new SortedSet<int>();
        var toolCounts  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var toolsByTurn = new SortedDictionary<int, List<string>>();
        var totalTools  = 0;
        var totalTurns  = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("session", out var sess) || sess.GetString() != ctx.SessionId) continue;
                if (!root.TryGetProperty("event_type", out var etEl)) continue;
                var et = etEl.GetString();

                if (et == "assistant_response")
                {
                    totalTurns++;
                    if (root.TryGetProperty("turn", out var tEl) && tEl.ValueKind == JsonValueKind.Number)
                        turnSet.Add(tEl.GetInt32());
                }

                if (et == EventTypes.ToolCall &&
                    root.TryGetProperty("payload", out var pl) &&
                    pl.TryGetProperty("tool_name", out var tn))
                {
                    var name    = tn.GetString() ?? "unknown";
                    var turnIdx = root.TryGetProperty("turn", out var tEl2) && tEl2.ValueKind == JsonValueKind.Number
                        ? tEl2.GetInt32() : -1;
                    toolCounts[name] = toolCounts.GetValueOrDefault(name) + 1;
                    totalTools++;
                    if (!toolsByTurn.ContainsKey(turnIdx)) toolsByTurn[turnIdx] = [];
                    toolsByTurn[turnIdx].Add(name);
                }
            }
            catch { /* skip malformed lines */ }
        }

        foreach (var t in turnSet)
            if (!toolsByTurn.ContainsKey(t)) toolsByTurn[t] = [];

        AnsiConsole.MarkupLine($"  [dim]Session:[/]     {Markup.Escape(ctx.SessionId)}");
        AnsiConsole.MarkupLine($"  [dim]Turns:[/]       {totalTurns}");
        AnsiConsole.MarkupLine($"  [dim]Tool calls:[/]  {totalTools}");

        if (toolsByTurn.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("  [dim]Per-turn breakdown:[/]");
            foreach (var (turn, tlist) in toolsByTurn)
            {
                var label = turn >= 0 ? $"turn {turn}" : "unknown";
                if (tlist.Count == 0)
                {
                    AnsiConsole.MarkupLine($"    [dim]{label}  (no tool calls)[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"    [dim]{label}  ({tlist.Count} call{(tlist.Count == 1 ? "" : "s")}):[/]");
                    foreach (var t in tlist)
                        AnsiConsole.MarkupLine($"      [dim]·[/] {Markup.Escape(t)}");
                }
            }
        }

        if (toolCounts.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("  [dim]Top tools:[/]");
            foreach (var (name, cnt) in toolCounts.OrderByDescending(kv => kv.Value).Take(10))
                AnsiConsole.MarkupLine($"    [dim]·[/] {Markup.Escape(name)}  [dim]{cnt}x[/]");
        }

        await ctx.Emitter.EmitAsync("command", payload: new { command = "/events stats" });
    }

    private static async Task<CommandResult> CmdSafeModeAsync(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            AnsiConsole.MarkupLine(ctx.SafeMode
                ? "[dim]Safe mode:[/] [green]on[/]  [dim](Shell, Git, Http disabled)[/]"
                : "[dim]Safe mode:[/] [dim]off[/]");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]/safe-mode on[/] [dim]or[/] [bold]/safe-mode off[/][dim].[/]");
            return CommandResult.Continue;
        }

        if (arg.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.SafeMode)
            {
                AnsiConsole.MarkupLine("[dim]Safe mode is already on.[/]");
            }
            else
            {
                ctx.PreSafeDisabled = new HashSet<string>(ctx.DisabledCategories, StringComparer.OrdinalIgnoreCase);
                foreach (var c in new[] { "Shell", "Git", "Http" }.Where(c => ctx.ToolsByCategory.ContainsKey(c)))
                    ctx.DisabledCategories.Add(c);
                ctx.ChatOptions = ctx.BuildChatOptions();
                ctx.SafeMode    = true;
                AnsiConsole.MarkupLine("[dim]Safe mode[/] [green]on[/][dim]: Shell, Git, Http tools disabled.[/]");
                await ctx.Emitter.EmitAsync("command", payload: new { command = "/safe-mode on" });
            }
        }
        else if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            if (!ctx.SafeMode)
            {
                AnsiConsole.MarkupLine("[dim]Safe mode is already off.[/]");
            }
            else
            {
                ctx.DisabledCategories.Clear();
                if (ctx.PreSafeDisabled is not null)
                    foreach (var c in ctx.PreSafeDisabled) ctx.DisabledCategories.Add(c);
                ctx.PreSafeDisabled = null;
                ctx.ChatOptions     = ctx.BuildChatOptions();
                ctx.SafeMode        = false;
                AnsiConsole.MarkupLine("[dim]Safe mode[/] [dim]off[/][dim]: tool categories restored.[/]");
                await ctx.Emitter.EmitAsync("command", payload: new { command = "/safe-mode off" });
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown /safe-mode argument:[/] {Markup.Escape(arg)}");
            AnsiConsole.MarkupLine("[dim]Usage: /safe-mode     — show current status[/]");
            AnsiConsole.MarkupLine("[dim]       /safe-mode on  — disable Shell, Git, Http tools[/]");
            AnsiConsole.MarkupLine("[dim]       /safe-mode off — restore tool categories[/]");
        }
        return CommandResult.Continue;
    }

    private static CommandResult CmdAdversarial(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            AnsiConsole.MarkupLine(ctx.AdversarialMode
                ? "[dim]Adversarial mode:[/] [green]on[/]  [dim](critic agent reviews each /execute step)[/]"
                : "[dim]Adversarial mode:[/] [dim]off[/]");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]/adversarial on[/] [dim]or[/] [bold]/adversarial off[/][dim].[/]");
            return CommandResult.Continue;
        }

        if (arg.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.SubAgent is null)
            {
                AnsiConsole.MarkupLine("[yellow]Adversarial mode requires tools (started with --no-tools).[/]");
                return CommandResult.Continue;
            }
            ctx.AdversarialMode = true;
            AnsiConsole.MarkupLine("[dim]Adversarial mode[/] [green]on[/][dim]: critic agent will review each /execute step.[/]");
            _ = ctx.Emitter.EmitAsync("command", payload: new { command = "/adversarial on" });
        }
        else if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            ctx.AdversarialMode = false;
            AnsiConsole.MarkupLine("[dim]Adversarial mode[/] [dim]off[/][dim].[/]");
            _ = ctx.Emitter.EmitAsync("command", payload: new { command = "/adversarial off" });
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown /adversarial argument:[/] {Markup.Escape(arg)}");
            AnsiConsole.MarkupLine("[dim]Usage: /adversarial     — show current status[/]");
            AnsiConsole.MarkupLine("[dim]       /adversarial on  — enable critic agent for /execute steps[/]");
            AnsiConsole.MarkupLine("[dim]       /adversarial off — disable critic agent[/]");
        }
        return CommandResult.Continue;
    }

    private static async Task<CommandResult> CmdAssistAsync(
        ReplSessionContext ctx, CancellationToken cancellationToken)
    {
        if (ctx.SubAgent is null)
        {
            AnsiConsole.MarkupLine("[dim]Sub-agent not available (started with --no-tools).[/]");
            return CommandResult.Continue;
        }
        if (ctx.TurnIndex == 0)
        {
            AnsiConsole.MarkupLine("[dim]No conversation yet — nothing to diagnose.[/]");
            return CommandResult.Continue;
        }

        // Spinner pollutes the captured JSON-mode output — skip it entirely there.
        var spinCts  = ctx.JsonMode ? null : new CancellationTokenSource();
        var spinTask = spinCts is not null
            ? ReplTurn.RunSpinnerAsync("diagnosing…", spinCts.Token)
            : Task.CompletedTask;
        try
        {
            var correction = await ctx.SubAgent.DiagnoseAsync(ctx.History, cancellationToken);
            if (spinCts is not null) { spinCts.Cancel(); await spinTask; ReplTurn.ClearSpinnerLine(); }

            if (correction is null)
            {
                AnsiConsole.MarkupLine("[dim]Diagnosis returned no output.[/]");
                return CommandResult.Continue;
            }

            // In JSON mode the correction text is injected silently; the webview will see the
            // AI's streamed response as a fresh assistant bubble via the SendInput path.
            if (!ctx.JsonMode)
            {
                AnsiConsole.MarkupLine("[dim]assist →[/]");
                AnsiConsole.WriteLine(correction);
                AnsiConsole.WriteLine();
            }
            await ctx.Emitter.EmitAsync("command", payload: new { command = "/assist" });
            return CommandResult.Send(correction);
        }
        catch (OperationCanceledException)
        {
            if (spinCts is not null) { spinCts.Cancel(); await spinTask; ReplTurn.ClearSpinnerLine(); }
            AnsiConsole.MarkupLine("[dim](cancelled)[/]");
            return CommandResult.Continue;
        }
        catch (Exception ex)
        {
            if (spinCts is not null) { spinCts.Cancel(); await spinTask; ReplTurn.ClearSpinnerLine(); }
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return CommandResult.Continue;
        }
    }

    private static async Task<CommandResult> CmdMemoryAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        var parts  = arg.Split(' ', 2, StringSplitOptions.TrimEntries);
        var sub    = parts[0].ToLowerInvariant();
        var memArg = parts.Length > 1 ? parts[1] : string.Empty;

        if (string.IsNullOrEmpty(arg) || sub == "list")
        {
            var all = await ctx.MemoryStore.LoadAllAsync(ctx.Cwd, ctx.SessionId);
            if (all.Count == 0)
                AnsiConsole.MarkupLine("[dim]No memories stored. They are saved automatically on /exit.[/]");
            else
            {
                AnsiConsole.MarkupLine($"[dim]{all.Count} memor{(all.Count == 1 ? "y" : "ies")} stored:[/]");
                foreach (var me in all.OrderBy(e => e.Type).ThenBy(e => e.Name))
                    AnsiConsole.MarkupLine(
                        $"  [dim][[{Markup.Escape(me.Type)}]][/] [bold]{Markup.Escape(me.Name)}[/] — {Markup.Escape(me.Description)}");
            }
        }
        else if (sub == "show")
        {
            if (string.IsNullOrEmpty(memArg))
            {
                AnsiConsole.MarkupLine("[yellow]Usage: /memory show <name>[/]");
            }
            else
            {
                var all   = await ctx.MemoryStore.LoadAllAsync(ctx.Cwd, ctx.SessionId);
                var found = all.FirstOrDefault(e => e.Name.Equals(memArg, StringComparison.OrdinalIgnoreCase));
                if (found is null)
                    AnsiConsole.MarkupLine($"[yellow]No memory named '{Markup.Escape(memArg)}'.[/]");
                else
                {
                    AnsiConsole.MarkupLine($"[bold]{Markup.Escape(found.Name)}[/] [dim]({Markup.Escape(found.Type)})[/]");
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(found.Description)}[/]");
                    AnsiConsole.WriteLine();
                    Console.WriteLine(found.Body);
                }
            }
        }
        else if (sub == "delete")
        {
            if (string.IsNullOrEmpty(memArg))
            {
                AnsiConsole.MarkupLine("[yellow]Usage: /memory delete <name>[/]");
            }
            else
            {
                var deleted = await ctx.MemoryStore.DeleteAsync(memArg, ctx.Cwd, sessionId: ctx.SessionId);
                AnsiConsole.MarkupLine(deleted
                    ? $"[dim]Deleted memory '{Markup.Escape(memArg)}'.[/]"
                    : $"[yellow]No memory named '{Markup.Escape(memArg)}'.[/]");
                await ctx.Emitter.EmitAsync("command", payload: new { command = "/memory delete", name = memArg });
            }
        }
        else if (sub == "save")
        {
            if (ctx.TurnIndex == 0)
            {
                AnsiConsole.MarkupLine("[dim]No conversation turns yet — nothing to extract.[/]");
            }
            else
            {
                if (!ctx.JsonMode) AnsiConsole.Markup("[dim]extracting memories…[/]");
                try
                {
                    var mc = ctx.Factory.Create(ctx.ModelConfig);
                    using var _ = mc as IDisposable;
                    var existing             = await ctx.MemoryStore.LoadAllAsync(ctx.Cwd, ctx.SessionId);
                    var (saved, parseFailed) = await new MemoryExtractor(mc).ExtractAsync([.. ctx.History], existing);
                    if (!ctx.JsonMode) Console.Write($"\r{new string(' ', 30)}\r");
                    foreach (var m in saved) await ctx.MemoryStore.SaveAsync(m, ctx.Cwd, sessionId: ctx.SessionId);
                    AnsiConsole.MarkupLine(parseFailed
                        ? "[dim](extraction returned unparseable output — memories may not have been saved)[/]"
                        : saved.Count > 0
                            ? $"[dim]{saved.Count} memor{(saved.Count == 1 ? "y" : "ies")} saved.[/]"
                            : "[dim]Nothing worth saving found.[/]");
                    ctx.LastExtractedTurnIndex = ctx.TurnIndex;
                    await ctx.Emitter.EmitAsync("command", payload: new
                        { command = "/memory save", saved = saved.Count, parseFailed });
                }
                catch (Exception ex)
                {
                    if (!ctx.JsonMode) Console.Write($"\r{new string(' ', 30)}\r");
                    AnsiConsole.MarkupLine($"[red]Memory extraction failed:[/] {Markup.Escape(ex.Message)}");
                }
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown /memory subcommand:[/] {Markup.Escape(sub)}");
            AnsiConsole.MarkupLine("[dim]Usage: /memory               — list memories[/]");
            AnsiConsole.MarkupLine("[dim]       /memory list          — same[/]");
            AnsiConsole.MarkupLine("[dim]       /memory show <name>   — show full memory[/]");
            AnsiConsole.MarkupLine("[dim]       /memory delete <name> — delete a memory[/]");
            AnsiConsole.MarkupLine("[dim]       /memory save          — extract and save now[/]");
        }
        return CommandResult.Continue;
    }

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
        await ctx.Emitter.EmitAsync("command", payload: new { command = "/compact", arg });
        return CommandResult.Continue;
    }

    /// <summary>
    /// Core compaction logic shared by the /compact command and the compact_context tool.
    /// Generates a handoff summary via LLM, replaces ctx.History, and resets per-turn
    /// metrics. Returns (success, errorReason, tokensBefore, tokensAfter).
    /// </summary>
    internal static async Task<(bool Success, string? ErrorReason, int BeforeEst, int AfterEst)>
        CompactHistoryAsync(ReplSessionContext ctx, string? focus, CancellationToken cancellationToken)
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
        await ctx.Emitter.EmitAsync("compaction", payload: new
        {
            source        = "manual",
            before_tokens = beforeEst,
            after_tokens  = afterEst,
            focus,
        });
        return (true, null, beforeEst, afterEst);
    }

    private static async Task<CommandResult> CmdExploreAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        if (ctx.SubAgent is null)
        {
            AnsiConsole.MarkupLine("[dim]Sub-agent not available (started with --no-tools).[/]");
            return CommandResult.Continue;
        }
        if (string.IsNullOrWhiteSpace(arg))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /explore <query>[/]");
            return CommandResult.Continue;
        }

        var spinCts       = ctx.JsonMode ? null : new CancellationTokenSource();
        var spinTask      = spinCts is not null
            ? ReplTurn.RunSpinnerAsync("exploring…", spinCts.Token)
            : Task.CompletedTask;
        bool spinStopped  = false;
        bool headerPrinted = false;

        async Task StopSpinner()
        {
            if (spinStopped || spinCts is null) return;
            spinStopped = true;
            spinCts.Cancel();
            await spinTask;
            ReplTurn.ClearSpinnerLine();
        }

        try
        {
            await ctx.SubAgent.ExploreStreamingAsync(arg,
                async chunk =>
                {
                    if (!headerPrinted)
                    {
                        headerPrinted = true;
                        await StopSpinner();
                        if (!ctx.JsonMode) AnsiConsole.MarkupLine("[dim]assistant:[/]");
                    }
                    await ReplTurn.WriteChunkSmoothAsync(chunk, cancellationToken);
                },
                cancellationToken: cancellationToken);

            await StopSpinner();
            if (headerPrinted) { if (!ctx.JsonMode) AnsiConsole.WriteLine(); }
            else AnsiConsole.MarkupLine("[dim](no output)[/]");
            await ctx.Emitter.EmitAsync("command", payload: new { command = "/explore", query = arg });
        }
        catch (OperationCanceledException)
        {
            await StopSpinner();
            AnsiConsole.MarkupLine("[dim](cancelled)[/]");
        }
        catch (Exception ex)
        {
            await StopSpinner();
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
        }

        if (!ctx.JsonMode) AnsiConsole.WriteLine();
        return CommandResult.Continue;
    }

    private static async Task<CommandResult> CmdLocateAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        if (ctx.SubAgent is null)
        {
            AnsiConsole.MarkupLine("[dim]Sub-agent not available (started with --no-tools).[/]");
            return CommandResult.Continue;
        }
        if (string.IsNullOrWhiteSpace(arg))
        {
            AnsiConsole.MarkupLine("[yellow]Usage: /locate <symbol>[/]");
            return CommandResult.Continue;
        }

        var spinCts      = ctx.JsonMode ? null : new CancellationTokenSource();
        var spinTask     = spinCts is not null
            ? ReplTurn.RunSpinnerAsync("locating…", spinCts.Token)
            : Task.CompletedTask;
        bool spinStopped = false;
        bool gotOutput   = false;

        async Task StopSpinner()
        {
            if (spinStopped || spinCts is null) return;
            spinStopped = true;
            spinCts.Cancel();
            await spinTask;
            ReplTurn.ClearSpinnerLine();
        }

        try
        {
            await ctx.SubAgent.LocateStreamingAsync(arg,
                async chunk =>
                {
                    if (!gotOutput)
                    {
                        gotOutput = true;
                        await StopSpinner();
                    }
                    await ReplTurn.WriteChunkSmoothAsync(chunk, cancellationToken);
                },
                cancellationToken: cancellationToken);

            await StopSpinner();
            if (gotOutput) { if (!ctx.JsonMode) AnsiConsole.WriteLine(); }
            else AnsiConsole.MarkupLine("[dim](not found)[/]");
            await ctx.Emitter.EmitAsync("command", payload: new { command = "/locate", target = arg });
        }
        catch (OperationCanceledException)
        {
            await StopSpinner();
            AnsiConsole.MarkupLine("[dim](cancelled)[/]");
        }
        catch (Exception ex)
        {
            await StopSpinner();
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
        }

        if (!ctx.JsonMode) AnsiConsole.WriteLine();
        return CommandResult.Continue;
    }

    private static async Task<CommandResult> CmdSwitchAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            AnsiConsole.MarkupLine("[dim]Usage: /switch <session-id>[/]");
            AnsiConsole.MarkupLine("[dim]Run /sessions to list available sessions.[/]");
            return CommandResult.Continue;
        }

        var targetId = arg.Trim();
        if (targetId.Equals(ctx.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[dim]Already in this session.[/]");
            return CommandResult.Continue;
        }

        // Checkpoint the current session before leaving it.
        await ReplTurn.SaveSnapshotAsync(ctx);

        var snapshot = await ReplSessionSnapshot.LoadAsync(targetId, cancellationToken);
        if (snapshot is null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No saved session found with ID '[bold]{Markup.Escape(targetId)}[/]'.[/]");
            AnsiConsole.MarkupLine("[dim]Run /sessions to list available sessions.[/]");
            return CommandResult.Continue;
        }

        var prevId    = ctx.SessionId;
        var prevModel = ctx.ModelId;

        // Switch model when the target session used a different one.
        if (!snapshot.ModelId.Equals(ctx.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            var hasTools  = ctx.GetActiveTools().Count > 0;
            var newConfig = ReplFactory.BuildModelConfig(snapshot.ModelId, ctx.UserCfg);
            try
            {
                var newClient     = ReplFactory.BuildClient(newConfig, ctx.Factory, hasTools);
                var newStepClient = ReplFactory.BuildClient(newConfig, ctx.Factory, hasTools, ReplTurn.StepIterationLimit);
                ctx.ModelId     = snapshot.ModelId;
                ctx.ModelConfig = newConfig;
                ctx.Client      = newClient;
                ctx.StepClient  = newStepClient;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]⚠ Could not switch to model {Markup.Escape(snapshot.ModelId)}: {Markup.Escape(ex.Message)}[/]");
                AnsiConsole.MarkupLine($"[dim]Keeping current model: {Markup.Escape(ctx.ModelId)}[/]");
            }
        }

        // Switch session identity.
        ctx.SessionId = snapshot.SessionId;
        ctx.StartedAt = snapshot.StartedAt;
        ctx.Emitter.SetSessionId(snapshot.SessionId);

        // Restore history; keep the current system prompt so memories and AGENTS.md
        // stay fresh (same approach as --resume at startup).
        var restored   = snapshot.RestoreHistory();
        var currentSys = ctx.History.FirstOrDefault(m => m.Role == ChatRole.System);
        if (restored.Count > 0 && restored[0].Role == ChatRole.System && currentSys is not null)
            restored[0] = currentSys;
        ctx.History.Clear();
        ctx.History.AddRange(restored);

        // Reset counters and plan state.
        ctx.TurnIndex              = snapshot.TurnIndex;
        ctx.PrevTurnTokenEstimate  = 0;
        ctx.PrevCtxEstimate        = 0;
        ctx.TurnTokenDeltas.Clear();
        ctx.LastExtractedTurnIndex = -1;
        ctx.ContextWarningShown    = false;
        ctx.ResetPlanState();

        // Restore plan execution state from the snapshot.
        if (snapshot.ExecutionQueue is { Length: > 0 })
            foreach (var e in snapshot.ExecutionQueue)
                ctx.ExecutionQueue.Enqueue((e.Step, e.Total));
        else if (snapshot.PendingPlan is { Length: > 0 })
            ctx.CurrentPlan = snapshot.PendingPlan;

        if (snapshot.HaltedAt is not null)
        {
            ctx.HaltedAt = (snapshot.HaltedAt.Step, snapshot.HaltedAt.Total);
            if (snapshot.HaltedRemaining is { Length: > 0 })
                foreach (var e in snapshot.HaltedRemaining)
                    ctx.HaltedRemaining.Enqueue((e.Step, e.Total));
            ctx.HaltedToolCalls = [.. snapshot.HaltedToolCalls ?? []];
            ctx.RecoveryHint    = snapshot.RecoveryHint;
        }

        var modelChanged = !ctx.ModelId.Equals(prevModel, StringComparison.OrdinalIgnoreCase);

        if (ctx.JsonMode)
        {
            Console.WriteLine(
                $"## Switched Session\n\n" +
                $"Now running as: **`{snapshot.SessionId}`** (was `{prevId}`)\n\n" +
                $"Model: {ctx.ModelId} · {snapshot.TurnIndex} turn{(snapshot.TurnIndex == 1 ? "" : "s")} · " +
                $"started {snapshot.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[dim]Switched to:[/] [bold cyan]{Markup.Escape(snapshot.SessionId)}[/]  " +
                $"[dim](was {Markup.Escape(prevId)})[/]");
            AnsiConsole.MarkupLine(
                $"[dim]Model:[/] [bold]{Markup.Escape(ctx.ModelId)}[/]" +
                (modelChanged ? $"  [dim](was {Markup.Escape(prevModel)})[/]" : string.Empty));
            AnsiConsole.MarkupLine(
                $"[dim]{snapshot.TurnIndex} turn{(snapshot.TurnIndex == 1 ? "" : "s")} · " +
                $"started {snapshot.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}[/]");

            if (ctx.ExecutionQueue.Count > 0)
                AnsiConsole.MarkupLine(
                    $"[dim]  Plan in progress: {ctx.ExecutionQueue.Count} step{(ctx.ExecutionQueue.Count == 1 ? "" : "s")} queued — resuming automatically[/]");
            else if (ctx.CurrentPlan is { Length: > 0 })
                AnsiConsole.MarkupLine(
                    $"[dim]  Pending plan restored ({ctx.CurrentPlan.Length} step{(ctx.CurrentPlan.Length == 1 ? "" : "s")}). Run /execute to start.[/]");

            if (ctx.HaltedAt is not null)
                AnsiConsole.MarkupLine(
                    $"[yellow]  ⚠ Plan halted at step {ctx.HaltedAt.Value.Step.Step} of {ctx.HaltedAt.Value.Total}. Run /recover or /resume.[/]");
        }

        await ctx.Emitter.EmitAsync("command", payload: new
        {
            command   = "/switch",
            target_id = snapshot.SessionId,
            prev_id   = prevId,
            turns     = snapshot.TurnIndex,
            model     = ctx.ModelId,
        });
        return CommandResult.Continue;
    }

    private static void CmdConversation(ReplSessionContext ctx)
    {
        // Collect (userMessage, assistantMessage?) pairs from the non-system history.
        var nonSys = ctx.History.Where(m => m.Role != ChatRole.System).ToList();
        var turns  = new List<(string User, string? Asst)>();
        for (var i = 0; i < nonSys.Count; i++)
        {
            if (nonSys[i].Role != ChatRole.User || IsStepSummary(nonSys[i])) continue;
            var userText = nonSys[i].Text ?? string.Empty;
            string? asstText = null;
            if (i + 1 < nonSys.Count && nonSys[i + 1].Role == ChatRole.Assistant)
            {
                asstText = nonSys[++i].Text;
            }
            turns.Add((userText, asstText));
        }

        if (turns.Count == 0)
        {
            if (ctx.JsonMode)
                Console.WriteLine("No conversation yet.");
            else
                AnsiConsole.MarkupLine("[dim]No conversation yet.[/]");
            return;
        }

        // Check whether early messages were trimmed (TrimHistory evicts old turns to fit context).
        var trimmed = ctx.TurnIndex > turns.Count;

        if (ctx.JsonMode)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## Conversation ({turns.Count} turn{(turns.Count == 1 ? "" : "s")}{(trimmed ? ", earlier turns trimmed" : "")})\n");
            for (var t = 0; t < turns.Count; t++)
            {
                var (u, a) = turns[t];
                var uPrev = u.Replace('\n', ' ').Trim();
                if (uPrev.Length > 100) uPrev = uPrev[..100] + "…";
                sb.AppendLine($"**{t + 1}.** *you:* {uPrev}");
                if (a is not null)
                {
                    var aPrev = a.Replace('\n', ' ').Trim();
                    if (aPrev.Length > 100) aPrev = aPrev[..100] + "…";
                    sb.AppendLine($"   *asst:* {aPrev}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("Use `/rewind <n>` to rewind to after turn n, or `/rewind -<n>` to go back n turns.");
            Console.Write(sb.ToString());
            return;
        }

        AnsiConsole.MarkupLine(trimmed
            ? $"[dim]{turns.Count} turn{(turns.Count == 1 ? "" : "s")} in memory  [yellow](earlier turns were trimmed to fit context)[/][dim]:[/]"
            : $"[dim]{turns.Count} turn{(turns.Count == 1 ? "" : "s")}:[/]");
        AnsiConsole.WriteLine();

        for (var t = 0; t < turns.Count; t++)
        {
            var (u, a) = turns[t];
            var uPrev = u.Replace('\n', ' ').Trim();
            if (uPrev.Length > 80) uPrev = uPrev[..80] + "…";
            AnsiConsole.MarkupLine($"  [bold]{t + 1,3}[/]  [cyan]you:[/] {Markup.Escape(uPrev)}");
            if (a is not null)
            {
                var aPrev = a.Replace('\n', ' ').Trim();
                if (aPrev.Length > 80) aPrev = aPrev[..80] + "…";
                AnsiConsole.MarkupLine($"       [dim]asst: {Markup.Escape(aPrev)}[/]");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]  /rewind <n>   — keep turns 1…n, discard the rest[/]");
        AnsiConsole.MarkupLine("[dim]  /rewind -<n>  — step back n turns from current[/]");
    }

    private static async Task<CommandResult> CmdRewindAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            AnsiConsole.MarkupLine("[dim]Usage: /rewind <n>   — keep turns 1…n, discard the rest[/]");
            AnsiConsole.MarkupLine("[dim]       /rewind -<n>  — step back n turns from current[/]");
            AnsiConsole.MarkupLine("[dim]Run /conversation to see turn numbers.[/]");
            return CommandResult.Continue;
        }

        // Use the count of User messages in history as the authoritative turn count —
        // TurnIndex can drift from the live history after TrimHistory or /execute steps.
        var nonSys     = ctx.History.Where(m => m.Role != ChatRole.System).ToList();
        var totalTurns = nonSys.Count(m => m.Role == ChatRole.User && !IsStepSummary(m));

        if (totalTurns == 0)
        {
            AnsiConsole.MarkupLine("[dim]No conversation to rewind.[/]");
            return CommandResult.Continue;
        }

        int targetTurn;
        if (arg.StartsWith('-'))
        {
            if (!int.TryParse(arg[1..], out var back) || back < 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Invalid /rewind argument:[/] {Markup.Escape(arg)}");
                return CommandResult.Continue;
            }
            targetTurn = totalTurns - back;
        }
        else
        {
            if (!int.TryParse(arg, out targetTurn) || targetTurn < 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Invalid /rewind argument:[/] {Markup.Escape(arg)}");
                return CommandResult.Continue;
            }
        }

        // Clamp to valid range — never underflow below 0 or past current end.
        targetTurn = Math.Clamp(targetTurn, 0, totalTurns);

        if (targetTurn == totalTurns)
        {
            AnsiConsole.MarkupLine($"[dim]Already at turn {totalTurns} — nothing to rewind.[/]");
            return CommandResult.Continue;
        }

        // Rebuild history: system prompt + first targetTurn user/assistant pairs.
        // Non-user messages (assistant responses) are kept with the turn they follow.
        var sys  = ctx.History.FirstOrDefault(m => m.Role == ChatRole.System);
        var kept = new List<ChatMessage>();
        if (sys is not null) kept.Add(sys);

        var seen = 0;
        for (var i = 0; i < nonSys.Count; i++)
        {
            if (nonSys[i].Role == ChatRole.User && !IsStepSummary(nonSys[i]))
            {
                if (seen >= targetTurn) break;
                kept.Add(nonSys[i]);
                seen++;
            }
            else
            {
                kept.Add(nonSys[i]); // assistant, tool, or step-summary — belongs to the preceding turn
            }
        }

        var removed = totalTurns - targetTurn;
        ctx.History.Clear();
        ctx.History.AddRange(kept);
        ctx.TurnIndex             = targetTurn;
        ctx.PrevTurnTokenEstimate = 0;
        ctx.PrevCtxEstimate       = 0;
        if (ctx.TurnTokenDeltas.Count > targetTurn)
            ctx.TurnTokenDeltas.RemoveRange(targetTurn, ctx.TurnTokenDeltas.Count - targetTurn);
        ctx.ResetPlanState();

        if (ctx.JsonMode)
        {
            Console.WriteLine(targetTurn == 0
                ? $"## Rewound to Start\n\nAll {removed} turn{(removed == 1 ? "" : "s")} removed."
                : $"## Rewound\n\nNow at turn {targetTurn}. {removed} turn{(removed == 1 ? "" : "s")} removed.");
        }
        else
        {
            AnsiConsole.MarkupLine(targetTurn == 0
                ? $"[dim]Rewound to start — {removed} turn{(removed == 1 ? "" : "s")} removed.[/]"
                : $"[dim]Rewound to after turn {targetTurn} — {removed} turn{(removed == 1 ? "" : "s")} removed.[/]");
        }

        await ctx.Emitter.EmitAsync("command", payload: new
            { command = "/rewind", target = targetTurn, removed, total_was = totalTurns });
        return CommandResult.Continue;
    }

    private static async Task<CommandResult> CmdForkAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        var doSwitch = arg.Equals("switch", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(arg) && !doSwitch)
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown /fork argument:[/] {Markup.Escape(arg)}");
            AnsiConsole.MarkupLine("[dim]Usage: /fork         — snapshot current session to a new ID[/]");
            AnsiConsole.MarkupLine("[dim]       /fork switch  — fork and immediately become the fork[/]");
            return CommandResult.Continue;
        }

        // Generate a fresh session ID for the fork.
        var bytes = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var forkId = Convert.ToHexString(bytes).ToLowerInvariant();

        // Snapshot current execution queue / halted state.
        var execQueue = ctx.ExecutionQueue.Count > 0
            ? [.. ctx.ExecutionQueue.Select(e => new PlanStepEntry(e.Step, e.Total))]
            : (PlanStepEntry[]?)null;

        var haltedAt = ctx.HaltedAt.HasValue
            ? new PlanStepEntry(ctx.HaltedAt.Value.Step, ctx.HaltedAt.Value.Total)
            : (PlanStepEntry?)null;

        var haltedRemaining = ctx.HaltedRemaining.Count > 0
            ? [.. ctx.HaltedRemaining.Select(e => new PlanStepEntry(e.Step, e.Total))]
            : (PlanStepEntry[]?)null;

        var snapshot = ReplSessionSnapshot.Capture(
            sessionId:       forkId,
            modelId:         ctx.ModelId,
            cwd:             ctx.Cwd,
            turnIndex:       ctx.TurnIndex,
            history:         ctx.History,
            startedAt:       DateTime.UtcNow,
            currentPlan:     ctx.CurrentPlan,
            executionQueue:  execQueue,
            haltedAt:        haltedAt,
            haltedRemaining: haltedRemaining,
            haltedToolCalls: ctx.HaltedToolCalls.Count > 0 ? [.. ctx.HaltedToolCalls] : null,
            recoveryHint:    ctx.RecoveryHint);

        try
        {
            await ReplSessionSnapshot.SaveAsync(snapshot, cancellationToken);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Fork failed:[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        if (doSwitch)
        {
            // The original session is already checkpointed on disk from the last turn's
            // auto-save.  Switch the live session to the fork by updating the mutable IDs.
            var prevId        = ctx.SessionId;
            ctx.SessionId     = forkId;
            ctx.StartedAt     = DateTime.UtcNow;
            ctx.Emitter.SetSessionId(forkId);

            if (ctx.JsonMode)
            {
                Console.WriteLine(
                    $"## Switched to Fork\n\n" +
                    $"Previous session: **`{prevId}`** (saved)\n\n" +
                    $"Now running as: **`{forkId}`**");
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]Switched to fork:[/] [bold cyan]{Markup.Escape(forkId)}[/]  [dim](was {Markup.Escape(prevId)})[/]");
            }

            await ctx.Emitter.EmitAsync("command", payload: new
                { command = "/fork switch", fork_id = forkId, prev_id = prevId, turns = ctx.TurnIndex });
        }
        else
        {
            if (ctx.JsonMode)
            {
                Console.WriteLine(
                    $"## Session Forked\n\n" +
                    $"New session ID: **`{forkId}`**\n\n" +
                    $"Resume with: `fuseraft repl --resume {forkId}`\n\n" +
                    $"Or use `/fork switch` to branch and continue as the fork immediately.");
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]Forked to:[/] [bold cyan]{Markup.Escape(forkId)}[/]  [dim]({ctx.TurnIndex} turn{(ctx.TurnIndex == 1 ? "" : "s")} copied)[/]");
                AnsiConsole.MarkupLine($"[dim]Resume with:[/] [bold]fuseraft repl --resume {Markup.Escape(forkId)}[/]");
                AnsiConsole.MarkupLine($"[dim]Or:[/] [bold]/fork switch[/] [dim]to branch and continue as the fork right now.[/]");
            }

            await ctx.Emitter.EmitAsync("command", payload: new
                { command = "/fork", fork_id = forkId, turns = ctx.TurnIndex });
        }

        return CommandResult.Continue;
    }

    private static async Task<CommandResult> CmdModelAsync(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            var effortDisplay = ctx.ModelConfig.ReasoningEffort is { } e
                ? $"  [dim]Reasoning:[/] [bold]{Markup.Escape(e)}[/]" : string.Empty;
            AnsiConsole.MarkupLine($"  [dim]Model:[/] [bold]{Markup.Escape(ctx.ModelId)}[/]{effortDisplay}");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]/model <id> [[effort]][/] [dim]to switch models. Effort: none, low, medium, high.[/]");
            return CommandResult.Continue;
        }

        // Optional second token is reasoning effort: /model grok-4.3 low
        var parts      = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var newModelId = parts[0];
        var newEffort  = parts.Length > 1 ? parts[1].ToLowerInvariant() : null;

        if (newEffort is not null and not ("none" or "low" or "medium" or "high"))
        {
            AnsiConsole.MarkupLine($"[red]✗ Invalid reasoning effort '{Markup.Escape(newEffort)}'.[/] [dim]Valid values: none, low, medium, high.[/]");
            return CommandResult.Continue;
        }

        if (newModelId.Equals(ctx.ModelId, StringComparison.OrdinalIgnoreCase)
            && newEffort == ctx.ModelConfig.ReasoningEffort)
        {
            AnsiConsole.MarkupLine($"[dim]Already using[/] [bold]{Markup.Escape(ctx.ModelId)}[/][dim].[/]");
            return CommandResult.Continue;
        }

        var newConfig = ReplFactory.BuildModelConfig(newModelId, ctx.UserCfg, newEffort);
        var hasTools  = ctx.GetActiveTools().Count > 0;
        IChatClient newClient;
        try
        {
            newClient = ReplFactory.BuildClient(newConfig, ctx.Factory, hasTools);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]✗ Could not create client for {Markup.Escape(newModelId)}:[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        var prevModel   = ctx.ModelId;
        ctx.ModelId     = newModelId;
        ctx.ModelConfig = newConfig;
        ctx.Client      = newClient;
        ctx.StepClient  = ReplFactory.BuildClient(newConfig, ctx.Factory, hasTools, ReplTurn.StepIterationLimit);

        // Keep the system message identity line current with the new model.
        var sysIdx = ctx.History.FindIndex(m => m.Role == ChatRole.System);
        if (sysIdx >= 0 && ctx.History[sysIdx].Text is { } sysText)
        {
            var updated = sysText.Replace(
                $"running on {prevModel}", $"running on {newModelId}",
                StringComparison.OrdinalIgnoreCase);
            ctx.History[sysIdx] = new ChatMessage(ChatRole.System, updated);
        }

        var effortSuffix = newEffort is not null ? $" [dim](reasoning: {Markup.Escape(newEffort)})[/]" : string.Empty;
        AnsiConsole.MarkupLine(
            $"[dim]Model:[/] [bold]{Markup.Escape(prevModel)}[/] [dim]→[/] [bold]{Markup.Escape(newModelId)}[/]{effortSuffix}  " +
            $"[dim](history preserved)[/]");
        await ctx.Emitter.EmitAsync("command", payload: new { command = "/model", model = newModelId, prev = prevModel, reasoning_effort = newEffort });
        return CommandResult.Continue;
    }

    private static readonly string[] ValidReasoningEfforts = ["none", "low", "medium", "high"];

    private static async Task<CommandResult> CmdReasoningAsync(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            var current = ctx.ModelConfig.ReasoningEffort ?? "(not set)";
            AnsiConsole.MarkupLine($"  [dim]Reasoning effort:[/] [bold]{Markup.Escape(current)}[/]");
            AnsiConsole.MarkupLine("[dim]Run[/] [bold]/reasoning <none|low|medium|high>[/] [dim]to change.[/]");
            return CommandResult.Continue;
        }

        var effort = arg.Trim().ToLowerInvariant();
        if (!ValidReasoningEfforts.Contains(effort))
        {
            AnsiConsole.MarkupLine($"[red]✗ Invalid value '{Markup.Escape(effort)}'.[/] [dim]Valid values: none, low, medium, high.[/]");
            return CommandResult.Continue;
        }

        var prev = ctx.ModelConfig.ReasoningEffort;
        if (effort == prev)
        {
            AnsiConsole.MarkupLine($"[dim]Reasoning effort already set to[/] [bold]{Markup.Escape(effort)}[/][dim].[/]");
            return CommandResult.Continue;
        }

        ctx.ModelConfig = ctx.ModelConfig with { ReasoningEffort = effort };
        var hasTools = ctx.GetActiveTools().Count > 0;
        try
        {
            ctx.Client     = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools);
            ctx.StepClient = ReplFactory.BuildClient(ctx.ModelConfig, ctx.Factory, hasTools, ReplTurn.StepIterationLimit);
        }
        catch (Exception ex)
        {
            ctx.ModelConfig = ctx.ModelConfig with { ReasoningEffort = prev };
            AnsiConsole.MarkupLine($"[red]✗ Could not apply reasoning effort:[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        var prevDisplay = prev ?? "(none)";
        AnsiConsole.MarkupLine($"[dim]Reasoning:[/] [bold]{Markup.Escape(prevDisplay)}[/] [dim]→[/] [bold]{Markup.Escape(effort)}[/]");
        await ctx.Emitter.EmitAsync("command", payload: new { command = "/reasoning", reasoning_effort = effort, prev = prevDisplay, model = ctx.ModelId });
        return CommandResult.Continue;
    }

    private static CommandResult CmdRetry(ReplSessionContext ctx)
    {
        var idx = ctx.History.FindLastIndex(m => m.Role == ChatRole.User);
        if (idx < 0)
        {
            AnsiConsole.MarkupLine("[dim]No previous message to retry.[/]");
            return CommandResult.Continue;
        }

        var lastUserText = ctx.History[idx].Text ?? string.Empty;

        // Remove the last user message and any trailing assistant response.
        ctx.History.RemoveRange(idx, ctx.History.Count - idx);

        // Un-count the retried turn so TurnIndex stays accurate after ExecuteAsync re-increments.
        if (ctx.TurnIndex > 0) ctx.TurnIndex--;

        if (ctx.JsonMode)
            Console.WriteLine($"Retrying: {lastUserText.Replace('\n', ' ').Trim()[..Math.Min(80, lastUserText.Length)]}…");
        else
            AnsiConsole.MarkupLine("[dim]Retrying last message…[/]");

        _ = ctx.Emitter.EmitAsync("command", payload: new { command = "/retry" });
        return CommandResult.Send(lastUserText);
    }

    private static void CmdLast(ReplSessionContext ctx)
    {
        var lastAsst = ctx.History.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastAsst is null)
        {
            if (ctx.JsonMode)
                Console.WriteLine("No assistant response yet.");
            else
                AnsiConsole.MarkupLine("[dim]No assistant response yet.[/]");
            return;
        }

        var text = lastAsst.Text ?? string.Empty;

        if (ctx.JsonMode)
        {
            Console.WriteLine(text);
            return;
        }

        AnsiConsole.MarkupLine("[dim]assistant (last response):[/]");
        AnsiConsole.Write(MarkdownRenderer.Render(text));
        AnsiConsole.WriteLine();
    }

    private static async Task CmdSessionsAsync(bool jsonMode, CancellationToken cancellationToken)
    {
        var sessions = await ReplSessionSnapshot.ListAsync(cancellationToken);
        if (sessions.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No saved sessions found.[/]");
            return;
        }

        if (jsonMode)
        {
            Console.WriteLine($"## Saved Sessions ({sessions.Count})\n");
            foreach (var s in sessions)
            {
                var age   = DateTime.UtcNow - s.LastUpdatedAt;
                var label = age.TotalDays >= 1 ? $"{(int)age.TotalDays}d ago"
                          : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h ago"
                          : $"{(int)age.TotalMinutes}m ago";
                var turns = $"{s.TurnIndex} turn{(s.TurnIndex == 1 ? "" : "s")}";
                Console.WriteLine(
                    $"- **`{s.SessionId}`** — {s.ModelId}, {turns}, {label} *({Path.GetFileName(s.Cwd)})*");
            }
            Console.WriteLine();
            Console.WriteLine("Resume a session with `/resume` if it's already loaded, or restart the panel and select the session.");
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Saved sessions ({sessions.Count}):[/]");
        AnsiConsole.WriteLine();

        // Five-column grid: ID · model (capped at 22 chars) · turns · age · label.
        // All columns NoWrap so Spectre owns the layout rather than the terminal.
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(2, 0, 2, 0))); // ID
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 2, 0))); // model
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 2, 0))); // turns
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 2, 0))); // age
        grid.AddColumn(new GridColumn().NoWrap().Padding(new Padding(0, 0, 0, 0))); // label

        foreach (var s in sessions)
        {
            var elapsed = DateTime.UtcNow - s.LastUpdatedAt;
            var age     = elapsed.TotalDays  >= 1 ? $"{(int)elapsed.TotalDays}d ago"
                        : elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h ago"
                        :                           $"{(int)elapsed.TotalMinutes}m ago";
            var turns   = $"{s.TurnIndex} turn{(s.TurnIndex == 1 ? "" : "s")}";
            var model   = s.ModelId.Length > 22 ? s.ModelId[..21] + "…" : s.ModelId;
            var cwd     = Path.GetFileName(s.Cwd);

            grid.AddRow(
                $"[bold cyan]{Markup.Escape(s.SessionId)}[/]",
                $"[dim]{Markup.Escape(model)}[/]",
                $"[dim]{Markup.Escape(turns)}[/]",
                $"[dim]{Markup.Escape(age)}[/]",
                $"[dim]{Markup.Escape(cwd)}[/]");
        }

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]  Resume with:[/] [bold]fuseraft repl --resume <id>[/]");
    }

    // -------------------------------------------------------------------------
    // Display utilities used by command handlers
    // -------------------------------------------------------------------------

    private static void PrintHelp(bool jsonMode = false)
    {
        if (jsonMode)
        {
            Console.WriteLine("## REPL Commands\n");

            Console.WriteLine("### Session");
            Console.WriteLine("- `/help` — Show this help");
            Console.WriteLine("- `/sessions` — List resumable sessions with IDs and turn counts");
            Console.WriteLine("- `/fork` — Snapshot the current session to a new ID so you can branch from this point");
            Console.WriteLine("- `/fork switch` — Fork and immediately become the fork (continue under the new ID)");
            Console.WriteLine("- `/switch <id>` — Save the current session and load another saved session in its place");
            Console.WriteLine("- `/conversation` — List all turns with numbers so you can pick a rewind point");
            Console.WriteLine("- `/rewind <n>` — Keep turns 1…n and discard the rest");
            Console.WriteLine("- `/rewind -<n>` — Step back n turns from the current position");
            Console.WriteLine("- `/retry` — Resend the last message (useful when the response was poor)");
            Console.WriteLine("- `/last` — Re-print the last assistant response");
            Console.WriteLine("- `/clear` — Clear conversation history (keeps system prompt)");
            Console.WriteLine("- `/history` — Show condensed conversation history");
            Console.WriteLine("- `/assist` — Diagnose the conversation and inject a corrective message");
            Console.WriteLine("- `/exit` — Exit the REPL (auto-saves memories)\n");

            Console.WriteLine("### Orchestration");
            Console.WriteLine("- `/run <task>` — Run a task using `fuseraft run` and inject the result as context");
            Console.WriteLine("- `/run <file>` — Load task from a file and run it (prompts for config if multiple exist)\n");

            Console.WriteLine("### Planning");
            Console.WriteLine("- `/plan <task>` — Create a structured plan (JSON steps, no tool calls)");
            Console.WriteLine("- `/plan` — Show the current stored plan");
            Console.WriteLine("- `/execute` — Run each plan step sequentially with postcondition checks");
            Console.WriteLine("- `/resume` — Retry the halted step and continue remaining steps");
            Console.WriteLine("- `/recover` — Inject failure context and retry the halted step with agent awareness\n");

            Console.WriteLine("### Tools & modes");
            Console.WriteLine("- `/tools` — List active tools by category");
            Console.WriteLine("- `/tools disable <category>` — Disable a tool category (FileSystem Shell Search Git Http)");
            Console.WriteLine("- `/tools enable <category>` — Re-enable a disabled tool category");
            Console.WriteLine("- `/safe-mode` — Show safe mode status");
            Console.WriteLine("- `/safe-mode on` — Disable Shell, Git, Http tools to prevent mutations");
            Console.WriteLine("- `/safe-mode off` — Restore tool categories");
            Console.WriteLine("- `/adversarial` — Show adversarial mode status");
            Console.WriteLine("- `/adversarial on` — Enable critic agent to review each `/execute` step");
            Console.WriteLine("- `/adversarial off` — Disable critic agent\n");

            Console.WriteLine("### Context & model");
            Console.WriteLine("- `/context` — Show estimated context window usage and per-category breakdown");
            Console.WriteLine("- `/compact` — Summarise conversation into a handoff doc and reset history");
            Console.WriteLine("- `/compact <focus>` — Same, but tailor the summary toward the next session's focus");
            Console.WriteLine("- `/model` — Show current model and reasoning effort");
            Console.WriteLine("- `/model <id> [effort]` — Switch model; optional effort: none, low, medium, high");
            Console.WriteLine("- `/reasoning` — Show current reasoning effort");
            Console.WriteLine("- `/reasoning <none|low|medium|high>` — Set reasoning effort for the current model");
            Console.WriteLine("- `/max-tokens <n>` — Set max output tokens for each response");
            Console.WriteLine("- `/max-tokens reset` — Restore provider default max output tokens");
            Console.WriteLine("- `/system` — Show current system prompt");
            Console.WriteLine("- `/system <prompt>` — Set a new system prompt");
            Console.WriteLine("- `/provider` — Show current provider, model, and API key\n");

            Console.WriteLine("### Memory");
            Console.WriteLine("- `/memory` — List all stored memories");
            Console.WriteLine("- `/memory show <name>` — Show full body of a memory");
            Console.WriteLine("- `/memory delete <name>` — Delete a stored memory");
            Console.WriteLine("- `/memory save` — Extract and save memories from the current session now\n");

            Console.WriteLine("### I/O & events");
            Console.WriteLine("- `/save` — Save transcript to `repl-<id>.md` in the current directory");
            Console.WriteLine("- `/save <file>` — Save transcript to the specified file");
            Console.WriteLine("- `/snapshot` — Write a full debug snapshot (context, tools, history, plan) to a temp file");
            Console.WriteLine("- `/events` — Show session event stats (turns, tool calls, top tools)");
            Console.WriteLine("- `/explore <query>` — Run a sub-agent exploration loop and return a prose summary");
            Console.WriteLine("- `/locate <symbol>` — Run a sub-agent symbol lookup; returns `path:line` result");
            return;
        }

        AnsiConsole.MarkupLine("[bold]REPL commands[/]");
        AnsiConsole.WriteLine();

        // Two-column grid: command (no-wrap, 2-space indent, 4-space gap) + description (wraps to terminal width).
        static Grid MakeGrid()
        {
            var g = new Grid();
            g.AddColumn(new GridColumn().NoWrap().Padding(new Padding(2, 0, 4, 0)));
            g.AddColumn(new GridColumn().Padding(new Padding(0, 0, 0, 0)));
            return g;
        }

        AnsiConsole.MarkupLine("  [dim]Session[/]");
        var session = MakeGrid();
        session.AddRow("[bold cyan]/help[/]",          "Show this help");
        session.AddRow("[bold cyan]/sessions[/]",      "List resumable sessions with IDs and turn counts");
        session.AddRow("[bold cyan]/fork[/]",           "Snapshot the current session to a new ID (branch from this point)");
        session.AddRow("[bold cyan]/fork switch[/]",    "Fork and immediately become the fork (continue under the new ID)");
        session.AddRow("[bold cyan]/switch <id>[/]",    "Save the current session and load another saved session in its place");
        session.AddRow("[bold cyan]/conversation[/]",   "List all turns with numbers so you can pick a rewind point");
        session.AddRow("[bold cyan]/rewind <n>[/]",     "Keep turns 1…n and discard the rest");
        session.AddRow("[bold cyan]/rewind -<n>[/]",    "Step back n turns from the current position");
        session.AddRow("[bold cyan]/retry[/]",           "Resend the last message (useful when the response was poor)");
        session.AddRow("[bold cyan]/last[/]",            "Re-print the last assistant response");
        session.AddRow("[bold cyan]/clear[/]",          "Clear conversation history (keeps system prompt)");
        session.AddRow("[bold cyan]/history[/]",        "Show condensed conversation history");
        session.AddRow("[bold cyan]/assist[/]",         "Diagnose the conversation and inject a corrective message");
        session.AddRow("[bold cyan]/exit[/]",           "Exit the REPL (auto-saves memories)");
        AnsiConsole.Write(session);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]Orchestration[/]");
        var orch = MakeGrid();
        orch.AddRow("[bold cyan]/run <task>[/]",  "Run a task via `fuseraft run`; injects result as conversation context");
        orch.AddRow("[bold cyan]/run <file>[/]",  "Load task from a file and run it (prompts for config if multiple exist)");
        AnsiConsole.Write(orch);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]Planning[/]");
        var planning = MakeGrid();
        planning.AddRow("[bold cyan]/plan <task>[/]", "Create a structured plan (JSON steps, no tool calls)");
        planning.AddRow("[bold cyan]/plan[/]",         "Show the current stored plan");
        planning.AddRow("[bold cyan]/execute[/]",      "Run each plan step sequentially with postcondition checks");
        planning.AddRow("[bold cyan]/resume[/]",       "Retry the halted step and continue remaining steps");
        planning.AddRow("[bold cyan]/recover[/]",      "Inject failure context and retry the halted step with agent awareness");
        AnsiConsole.Write(planning);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]Tools & modes[/]");
        var tools = MakeGrid();
        tools.AddRow("[bold cyan]/tools[/]",                       "List active tools by category");
        tools.AddRow("[bold cyan]/tools disable <category>[/]",    "Disable a tool category (FileSystem Shell Search Git Http)");
        tools.AddRow("[bold cyan]/tools enable <category>[/]",     "Re-enable a disabled tool category");
        tools.AddRow("[bold cyan]/safe-mode[/]",                   "Show safe mode status");
        tools.AddRow("[bold cyan]/safe-mode on[/]",                "Disable Shell, Git, Http tools to prevent mutations");
        tools.AddRow("[bold cyan]/safe-mode off[/]",               "Restore tool categories");
        tools.AddRow("[bold cyan]/adversarial[/]",                 "Show adversarial mode status");
        tools.AddRow("[bold cyan]/adversarial on[/]",              "Enable critic agent to review each /execute step");
        tools.AddRow("[bold cyan]/adversarial off[/]",             "Disable critic agent");
        AnsiConsole.Write(tools);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]Context & model[/]");
        var ctx = MakeGrid();
        ctx.AddRow("[bold cyan]/context[/]",           "Show estimated context window usage and per-category breakdown");
        ctx.AddRow("[bold cyan]/compact[/]",            "Summarise conversation into a handoff doc and reset history");
        ctx.AddRow("[bold cyan]/compact <focus>[/]",    "Same, but tailor the summary toward the next session's focus");
        ctx.AddRow("[bold cyan]/model[/]",                          "Show current model and reasoning effort");
        ctx.AddRow("[bold cyan]/model <id> [[effort]][/]",          "Switch model; effort: none, low, medium, high");
        ctx.AddRow("[bold cyan]/reasoning[/]",                     "Show current reasoning effort");
        ctx.AddRow("[bold cyan]/reasoning <effort>[/]",            "Set reasoning effort for the current model");
        ctx.AddRow("[bold cyan]/max-tokens <n>[/]",     "Set max output tokens for each response");
        ctx.AddRow("[bold cyan]/max-tokens reset[/]",   "Restore provider default max output tokens");
        ctx.AddRow("[bold cyan]/system[/]",             "Show current system prompt");
        ctx.AddRow("[bold cyan]/system <prompt>[/]",    "Set a new system prompt");
        ctx.AddRow("[bold cyan]/provider[/]",           "Show current provider, model, and API key");
        ctx.AddRow("[bold cyan]/provider setup[/]",     "Reconfigure provider, model, and API key");
        AnsiConsole.Write(ctx);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]Memory[/]");
        var mem = MakeGrid();
        mem.AddRow("[bold cyan]/memory[/]",               "List all stored memories");
        mem.AddRow("[bold cyan]/memory show <name>[/]",   "Show full body of a memory");
        mem.AddRow("[bold cyan]/memory delete <name>[/]", "Delete a stored memory");
        mem.AddRow("[bold cyan]/memory save[/]",          "Extract and save memories from the current session now");
        AnsiConsole.Write(mem);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("  [dim]I/O & events[/]");
        var io = MakeGrid();
        io.AddRow("[bold cyan]/paste[/]",           "Enter paste mode (multi-line input; type EOF to finish)");
        io.AddRow("[bold cyan]/save[/]",             "Save transcript to repl-<id>.md in the current directory");
        io.AddRow("[bold cyan]/save <file>[/]",      "Save transcript to the specified file");
        io.AddRow("[bold cyan]/snapshot[/]",          "Write a full debug snapshot (context, tools, history, plan) to a temp file");
        io.AddRow("[bold cyan]/events[/]",           "Show session event stats (turns, tool calls, top tools)");
        io.AddRow("[bold cyan]/events stats[/]",     "Same as /events");
        io.AddRow("[bold cyan]/explore <query>[/]",  "Run a sub-agent exploration loop and return a prose summary");
        io.AddRow("[bold cyan]/locate <symbol>[/]",  "Run a sub-agent symbol lookup; returns path:line result");
        AnsiConsole.Write(io);
    }

    private static async Task<CommandResult> CmdRunAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        // Resolve task text — accept inline text or a path to a task file.
        if (string.IsNullOrWhiteSpace(arg))
        {
            if (ctx.JsonMode)
            {
                Console.WriteLine("Usage: `/run <task>` or `/run <path-to-task-file>`");
                return CommandResult.Continue;
            }
            AnsiConsole.Markup("[dim]Task (or path to task file): [/]");
            arg = Console.ReadLine()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(arg))
            {
                AnsiConsole.MarkupLine("[dim]No task provided.[/]");
                return CommandResult.Continue;
            }
        }

        string task;
        var absArg = Path.IsPathRooted(arg) ? arg : Path.GetFullPath(Path.Combine(ctx.Cwd, arg));
        if (File.Exists(absArg))
        {
            task = (await File.ReadAllTextAsync(absArg, cancellationToken)).Trim();
            if (string.IsNullOrWhiteSpace(task))
            {
                AnsiConsole.MarkupLine($"[red]✗ Task file is empty:[/] {Markup.Escape(absArg)}");
                return CommandResult.Continue;
            }
            if (!ctx.JsonMode)
                AnsiConsole.MarkupLine($"[dim]Task file:[/] {Markup.Escape(absArg)}");
        }
        else
        {
            task = arg;
        }

        var configPath = SelectRunConfig(ctx.Cwd, ctx.JsonMode);
        if (configPath is null)
            return CommandResult.Continue;

        var tmpTask = Path.Combine(Path.GetTempPath(), $"fuseraft-run-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tmpTask, task, System.Text.Encoding.UTF8, cancellationToken);

        try
        {
            var taskPreview = task.Length > 120 ? task[..120] + "…" : task;
            var configRel   = Path.GetRelativePath(ctx.Cwd, configPath);

            if (ctx.JsonMode)
                Console.WriteLine($"Running task with config `{configRel}`…\n");
            else
            {
                AnsiConsole.MarkupLine($"[dim]Config:[/] {Markup.Escape(configRel)}");
                AnsiConsole.MarkupLine($"[dim]Task:[/]   {Markup.Escape(taskPreview)}");
                AnsiConsole.WriteLine();
            }

            var exe = ResolveRunExe();
            var sw  = Stopwatch.StartNew();

            var (exitCode, output) = await RunOrchestrationSubprocessAsync(exe, configPath, tmpTask, cancellationToken);
            sw.Stop();

            var succeeded = exitCode == 0;
            var status    = succeeded ? "succeeded" : $"failed (exit code {exitCode})";

            if (ctx.JsonMode)
            {
                Console.WriteLine(succeeded
                    ? $"\n✓ Run succeeded ({sw.Elapsed.TotalSeconds:F1}s). Ask me what happened."
                    : $"\n✗ Run {status} ({sw.Elapsed.TotalSeconds:F1}s). Ask me what went wrong.");
            }
            else
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(succeeded
                    ? $"[green]✓ Run {status}[/] [dim]({sw.Elapsed.TotalSeconds:F1}s)[/]"
                    : $"[red]✗ Run {status}[/] [dim]({sw.Elapsed.TotalSeconds:F1}s)[/]");
                AnsiConsole.MarkupLine("[dim]Run context added to conversation — ask me what happened.[/]");
                AnsiConsole.WriteLine();
            }

            InjectRunContext(ctx, task, configPath, succeeded, exitCode, sw.Elapsed, output);

            await ctx.Emitter.EmitAsync("command", payload: new
            {
                command   = "/run",
                config    = configPath,
                succeeded,
                exit_code = exitCode,
                elapsed   = sw.Elapsed.TotalSeconds,
            });
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[dim](run cancelled)[/]");
            AnsiConsole.WriteLine();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ /run failed:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteLine();
        }
        finally
        {
            try { File.Delete(tmpTask); } catch { /* best effort */ }
        }

        return CommandResult.Continue;
    }

    private static void InjectRunContext(
        ReplSessionContext ctx, string task, string configPath,
        bool succeeded, int exitCode, TimeSpan elapsed, string output)
    {
        var taskPreview   = task.Length > 500  ? task[..500]   + "\n…(truncated)" : task;
        var outputPreview = output.Length > 3000 ? output[..3000] + "\n…(output truncated)" : output;
        var configRel     = Path.GetRelativePath(ctx.Cwd, configPath);
        var status        = succeeded ? "succeeded" : $"failed (exit code {exitCode})";

        var context =
            $"[Run result]\n" +
            $"Config:  {configRel}\n" +
            $"Task:    {taskPreview}\n" +
            $"Status:  {status}\n" +
            $"Elapsed: {elapsed.TotalSeconds:F1}s\n\n" +
            $"Output:\n```\n{outputPreview}\n```";

        ctx.History.Add(new ChatMessage(ChatRole.User, context));
        ctx.History.Add(new ChatMessage(ChatRole.Assistant,
            succeeded
                ? "The run completed successfully. I have the full output and can answer questions about what happened, what was produced, or what succeeded."
                : "The run failed. I have the captured output and can help diagnose what went wrong. Ask me about any specific error or step."));
    }

    private static string? SelectRunConfig(string cwd, bool jsonMode)
    {
        var configDir = Path.Combine(cwd, ".fuseraft", "config");

        if (!Directory.Exists(configDir))
            return Path.Combine(configDir, "orchestration.yaml");

        var configs = Directory.GetFiles(configDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".yml",  StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();

        if (configs.Count == 0)
            return Path.Combine(configDir, "orchestration.yaml");

        if (configs.Count == 1)
            return configs[0];

        // Multiple configs — in JSON mode just use the first; in terminal mode prompt.
        if (jsonMode)
        {
            var chosen = configs[0];
            Console.WriteLine($"Multiple configs found — using `{Path.GetRelativePath(cwd, chosen)}`.");
            Console.WriteLine("Re-run with `/run --config <path> <task>` to choose a different one.");
            return chosen;
        }

        AnsiConsole.MarkupLine($"[dim]{configs.Count} configs found — pick one:[/]");
        AnsiConsole.WriteLine();
        for (int i = 0; i < configs.Count; i++)
            AnsiConsole.MarkupLine($"  [bold cyan]{i + 1}.[/] {Markup.Escape(Path.GetRelativePath(cwd, configs[i]))}");
        AnsiConsole.WriteLine();
        AnsiConsole.Markup($"[dim]Select (1–{configs.Count}): [/]");

        var line = Console.ReadLine()?.Trim() ?? string.Empty;
        if (!int.TryParse(line, out var choice) || choice < 1 || choice > configs.Count)
        {
            AnsiConsole.MarkupLine("[yellow]Invalid selection — run cancelled.[/]");
            return null;
        }

        return configs[choice - 1];
    }

    private static async Task<(int ExitCode, string Output)> RunOrchestrationSubprocessAsync(
        string exe, string configPath, string taskFile, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var psi    = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(configPath);
        psi.ArgumentList.Add("--task-file");
        psi.ArgumentList.Add(taskFile);
        psi.ArgumentList.Add("--no-banner");

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = ForwardStreamAsync(proc.StandardOutput, output, Console.Out);
        var stderrTask = ForwardStreamAsync(proc.StandardError,  output, Console.Error);

        try
        {
            await proc.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return (proc.ExitCode, output.ToString());
    }

    private static async Task ForwardStreamAsync(
        System.IO.StreamReader reader, StringBuilder buffer, System.IO.TextWriter console)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            console.WriteLine(line);
            lock (buffer) buffer.AppendLine(line);
        }
    }

    private static string ResolveRunExe()
    {
        var pp = Environment.ProcessPath;
        if (pp is not null
            && !pp.EndsWith("dotnet",     StringComparison.OrdinalIgnoreCase)
            && !pp.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return pp;
        return "fuseraft";
    }

    private static async Task CmdSnapshotAsync(ReplSessionContext ctx)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(FuseraftPaths.SystemTempRoot, $"repl-snapshot-{ctx.SessionId}-{timestamp}.json");
        Directory.CreateDirectory(FuseraftPaths.SystemTempRoot);

        var snapshot = new
        {
            session = new
            {
                sessionId              = ctx.SessionId,
                modelId                = ctx.ModelId,
                cwd                    = ctx.Cwd,
                eventsPath             = ctx.EventsPath,
                startedAt              = ctx.StartedAt,
                capturedAt             = DateTime.UtcNow,
                turnIndex              = ctx.TurnIndex,
                lastExtractedTurnIndex = ctx.LastExtractedTurnIndex,
                pendingSave            = ctx.PendingSave,
            },
            modes = new
            {
                jsonMode        = ctx.JsonMode,
                safeMode        = ctx.SafeMode,
                adversarialMode = ctx.AdversarialMode,
                maxOutputTokens = ctx.MaxOutputTokens,
                verbose         = ctx.Verbose,
            },
            context = new
            {
                estimatedTokens       = ctx.EstimateTokens(),
                prevCtxEstimate       = ctx.PrevCtxEstimate,
                prevTurnTokenEstimate = ctx.PrevTurnTokenEstimate,
                turnTokenDeltas       = ctx.TurnTokenDeltas,
                contextWarningShown   = ctx.ContextWarningShown,
            },
            tools = new
            {
                disabledCategories = ctx.DisabledCategories.ToList(),
                activeCount        = ctx.GetActiveTools().Count,
                categories         = ctx.ToolsByCategory.Select(kv => new
                {
                    category = kv.Key,
                    disabled = ctx.DisabledCategories.Contains(kv.Key),
                    count    = kv.Value.Count,
                    tools    = kv.Value.Select(t => t.Name).ToList(),
                }).ToList(),
            },
            plan = ctx.CurrentPlan is null && ctx.ExecutionQueue.Count == 0 && ctx.HaltedAt is null
                ? (object?)null
                : new
                {
                    currentPlan     = ctx.CurrentPlan,
                    executionQueue  = ctx.ExecutionQueue.Select(e => new { step = e.Step, total = e.Total }).ToArray(),
                    haltedAt        = ctx.HaltedAt is { } h ? new { step = h.Step, total = h.Total } : (object?)null,
                    haltedRemaining = ctx.HaltedRemaining.Select(e => new { step = e.Step, total = e.Total }).ToArray(),
                    haltedToolCalls = ctx.HaltedToolCalls,
                    recoveryHint    = ctx.RecoveryHint,
                },
            history = ctx.History.Select(ReplSerializedMessage.From).ToList(),
        };

        var opts = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, opts));
        AnsiConsole.MarkupLine($"[green]Snapshot written:[/] {Markup.Escape(path)}");
    }

    private static void SaveTranscript(List<ChatMessage> history, string modelId, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# REPL Transcript");
        sb.AppendLine($"Model: {modelId}  ");
        sb.AppendLine($"Saved: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();

        foreach (var msg in history)
        {
            string? label = null;
            if      (msg.Role == ChatRole.System)    label = "**System**";
            else if (msg.Role == ChatRole.User)      label = "**User**";
            else if (msg.Role == ChatRole.Assistant) label = "**Assistant**";
            if (label is null) continue;
            sb.AppendLine("---");
            sb.AppendLine(label);
            sb.AppendLine();
            sb.AppendLine(msg.Text);
            sb.AppendLine();
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
    }

    private static void PrintContextRow(string label, int tokens, int total, string? note = null)
    {
        var pct         = total > 0 ? (double)tokens / total * 100.0 : 0.0;
        var bar         = new string('█', (int)(pct / 5)).PadRight(20, '░');
        var paddedLabel = label.PadRight(15);
        var suffix      = note is not null ? $" [dim]{Markup.Escape(note)}[/]" : string.Empty;
        AnsiConsole.MarkupLine(
            $"    [dim]{Markup.Escape(paddedLabel)}[/] [bold]{tokens,7:N0}[/] [dim]tok  {pct,5:F1}%  {bar}[/]{suffix}");
    }

    private static bool IsStepSummary(ChatMessage m) =>
        m.Role == ChatRole.User &&
        m.Text is { } t &&
        t.StartsWith("[Step ", StringComparison.Ordinal) &&
        t.Contains(" complete]", StringComparison.Ordinal);

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
