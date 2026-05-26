using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

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

        AnsiConsole.MarkupLine("[dim]Paste your content below. Type[/] [bold]EOF[/] [dim]on its own line when done.[/]");
        var lines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine();
            if (line is null || line == "EOF") break;
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
        var total = ctx.CurrentPlan.Length;
        foreach (var ps in ctx.CurrentPlan)
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

                if (et == "tool_call" &&
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
            var all = await ctx.MemoryStore.LoadAllAsync(ctx.Cwd);
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
                var all   = await ctx.MemoryStore.LoadAllAsync(ctx.Cwd);
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
                var deleted = await ctx.MemoryStore.DeleteAsync(memArg, ctx.Cwd);
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
                    var existing             = await ctx.MemoryStore.LoadAllAsync(ctx.Cwd);
                    var (saved, parseFailed) = await new MemoryExtractor(mc).ExtractAsync([.. ctx.History], existing);
                    if (!ctx.JsonMode) Console.Write($"\r{new string(' ', 30)}\r");
                    foreach (var m in saved) await ctx.MemoryStore.SaveAsync(m, ctx.Cwd);
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

        var focus = string.IsNullOrWhiteSpace(arg) ? string.Empty : $"\n\nFocus for the next session: {arg}";
        var compactionPrompt =
            "Write a concise handoff document summarising this conversation so a fresh session can continue the work. " +
            "Include: what was being worked on, key decisions and findings, current state, and what comes next. " +
            "Reference file paths and symbols by name rather than quoting their full content. " +
            "Redact any sensitive values such as API keys or passwords." +
            focus;

        var messages = new List<ChatMessage>(ctx.History)
        {
            new ChatMessage(ChatRole.User, compactionPrompt)
        };

        string summary;
        try
        {
            var mc       = ctx.Factory.Create(ctx.ModelConfig);
            using var _  = mc as IDisposable;
            var response = await mc.GetResponseAsync(messages, cancellationToken: cancellationToken);
            summary      = response.Text ?? string.Empty;
            if (!ctx.JsonMode) Console.Write($"\r{new string(' ', 30)}\r");

            if (string.IsNullOrWhiteSpace(summary))
            {
                AnsiConsole.MarkupLine("[yellow]Compaction returned empty output — history unchanged.[/]");
                return CommandResult.Continue;
            }
        }
        catch (OperationCanceledException)
        {
            if (!ctx.JsonMode) Console.Write($"\r{new string(' ', 30)}\r");
            AnsiConsole.MarkupLine("[dim](cancelled)[/]");
            return CommandResult.Continue;
        }
        catch (Exception ex)
        {
            if (!ctx.JsonMode) Console.Write($"\r{new string(' ', 30)}\r");
            AnsiConsole.MarkupLine($"[red]✗ Compaction failed:[/] {Markup.Escape(ex.Message)}");
            return CommandResult.Continue;
        }

        var sys = ctx.History.FirstOrDefault(m => m.Role == ChatRole.System);
        ctx.History.Clear();
        if (sys is not null) ctx.History.Add(sys);
        ctx.History.Add(new ChatMessage(ChatRole.User, $"[Compacted context from previous session]\n\n{summary}"));

        ctx.TurnIndex             = 0;
        ctx.PrevTurnTokenEstimate = 0;
        ctx.TurnTokenDeltas.Clear();
        ctx.ResetPlanState();

        AnsiConsole.MarkupLine("[dim]Session compacted — history replaced with handoff summary.[/]");
        await ctx.Emitter.EmitAsync("command", payload: new { command = "/compact", arg });
        return CommandResult.Continue;
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
            if (nonSys[i].Role != ChatRole.User) continue;
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
        var totalTurns = nonSys.Count(m => m.Role == ChatRole.User);

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
            if (nonSys[i].Role == ChatRole.User)
            {
                if (seen >= targetTurn) break;
                kept.Add(nonSys[i]);
                seen++;
            }
            else
            {
                kept.Add(nonSys[i]); // assistant message — belongs to the preceding user turn
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
        foreach (var s in sessions)
        {
            var age   = DateTime.UtcNow - s.LastUpdatedAt;
            var label = age.TotalDays >= 1
                ? $"{(int)age.TotalDays}d ago"
                : age.TotalHours >= 1
                    ? $"{(int)age.TotalHours}h ago"
                    : $"{(int)age.TotalMinutes}m ago";
            AnsiConsole.MarkupLine(
                $"  [bold cyan]{Markup.Escape(s.SessionId)}[/]  " +
                $"[dim]{Markup.Escape(s.ModelId)}  " +
                $"{s.TurnIndex} turn{(s.TurnIndex == 1 ? "" : "s")}  " +
                $"{Markup.Escape(label)}  " +
                $"{Markup.Escape(Path.GetFileName(s.Cwd))}[/]");
        }
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
            Console.WriteLine("- `/clear` — Clear conversation history (keeps system prompt)");
            Console.WriteLine("- `/history` — Show condensed conversation history");
            Console.WriteLine("- `/assist` — Diagnose the conversation and inject a corrective message");
            Console.WriteLine("- `/exit` — Exit the REPL (auto-saves memories)\n");

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
        session.AddRow("[bold cyan]/clear[/]",          "Clear conversation history (keeps system prompt)");
        session.AddRow("[bold cyan]/history[/]",        "Show condensed conversation history");
        session.AddRow("[bold cyan]/assist[/]",         "Diagnose the conversation and inject a corrective message");
        session.AddRow("[bold cyan]/exit[/]",           "Exit the REPL (auto-saves memories)");
        AnsiConsole.Write(session);
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
        io.AddRow("[bold cyan]/events[/]",           "Show session event stats (turns, tool calls, top tools)");
        io.AddRow("[bold cyan]/events stats[/]",     "Same as /events");
        io.AddRow("[bold cyan]/explore <query>[/]",  "Run a sub-agent exploration loop and return a prose summary");
        io.AddRow("[bold cyan]/locate <symbol>[/]",  "Run a sub-agent symbol lookup; returns path:line result");
        AnsiConsole.Write(io);
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
}
