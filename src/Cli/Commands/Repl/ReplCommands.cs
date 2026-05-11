using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Spectre.Console;
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
            case "/help":       PrintHelp(); return CommandResult.Continue;
            case "/clear":      return await CmdClearAsync(ctx);
            case "/system":     return CmdSystem(ctx, arg);
            case "/tools":      return await CmdToolsAsync(ctx, arg);
            case "/paste":      return CmdPaste();
            case "/save":       return await CmdSaveAsync(ctx, arg);
            case "/history":    CmdHistory(ctx); return CommandResult.Continue;
            case "/context":    await CmdContextAsync(ctx); return CommandResult.Continue;
            case "/provider":   return await CmdProviderAsync(ctx, arg);
            case "/plan":       return await CmdPlanAsync(ctx, arg);
            case "/execute":    return await CmdExecuteAsync(ctx);
            case "/resume":     return CmdResume(ctx);
            case "/recover":    return CmdRecover(ctx);
            case "/events":     await CmdEventsAsync(ctx, arg); return CommandResult.Continue;
            case "/safe-mode":  return await CmdSafeModeAsync(ctx, arg);
            case "/memory":     return await CmdMemoryAsync(ctx, arg, cancellationToken);
            case "/max-tokens": return CmdMaxTokens(ctx, arg);
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

    private static CommandResult CmdPaste()
    {
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
        var bar      = new string('█', (int)(pct / 5)).PadRight(20, '░');
        var deltaStr = ctx.PrevCtxEstimate > 0
            ? (total - ctx.PrevCtxEstimate is var d and >= 0
                ? $"  [dim](+{d:N0} since last check)[/]"
                : $"  [dim]({total - ctx.PrevCtxEstimate:N0} since last check)[/]")
            : string.Empty;

        AnsiConsole.MarkupLine(
            $"  [dim]Tokens (est.):[/] [bold]{total:N0}[/] / {ReplTurn.ContextTokenBudget:N0}  " +
            $"[{(pct >= 90 ? "red" : pct >= 70 ? "yellow" : "green")}]{Markup.Escape(bar)}[/]  " +
            $"[dim]{pct:F1}%[/]{deltaStr}");
        AnsiConsole.MarkupLine(
            $"  [dim]Messages:[/]     {ctx.History.Count}  " +
            $"[dim](system: {ctx.History.Count(m => m.Role == ChatRole.System)}, " +
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
                AnsiConsole.Markup("[dim]extracting memories…[/]");
                try
                {
                    var mc = ctx.Factory.Create(ctx.ModelConfig);
                    using var _ = mc as IDisposable;
                    var existing             = await ctx.MemoryStore.LoadAllAsync(ctx.Cwd);
                    var (saved, parseFailed) = await new MemoryExtractor(mc).ExtractAsync([.. ctx.History], existing);
                    Console.Write($"\r{new string(' ', 30)}\r");
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
                    Console.Write($"\r{new string(' ', 30)}\r");
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

    // -------------------------------------------------------------------------
    // Display utilities used by command handlers
    // -------------------------------------------------------------------------

    private static void PrintHelp()
    {
        AnsiConsole.MarkupLine("[bold]REPL commands[/]");
        AnsiConsole.MarkupLine("  [bold cyan]/help[/]                    Show this help");
        AnsiConsole.MarkupLine("  [bold cyan]/clear[/]                   Clear conversation history (keeps system prompt)");
        AnsiConsole.MarkupLine("  [bold cyan]/history[/]                 Show condensed conversation history");
        AnsiConsole.MarkupLine("  [bold cyan]/system[/]                  Show current system prompt");
        AnsiConsole.MarkupLine("  [bold cyan]/system <prompt>[/]         Set a new system prompt");
        AnsiConsole.MarkupLine("  [bold cyan]/tools[/]                   List active tools by category");
        AnsiConsole.MarkupLine("  [bold cyan]/tools disable <category>[/] Disable a tool category (FileSystem Shell Search Git Http)");
        AnsiConsole.MarkupLine("  [bold cyan]/tools enable <category>[/]  Re-enable a disabled tool category");
        AnsiConsole.MarkupLine("  [bold cyan]/paste[/]                   Enter paste mode (multi-line input; type EOF to finish)");
        AnsiConsole.MarkupLine("  [bold cyan]/save[/]                    Save transcript to repl-<id>.md in the current directory");
        AnsiConsole.MarkupLine("  [bold cyan]/save <file>[/]             Save transcript to the specified file");
        AnsiConsole.MarkupLine("  [bold cyan]/plan <task>[/]             Create a structured plan (JSON steps, no tool calls)");
        AnsiConsole.MarkupLine("  [bold cyan]/plan[/]                    Show the current stored plan");
        AnsiConsole.MarkupLine("  [bold cyan]/execute[/]                 Run each plan step sequentially with postcondition checks");
        AnsiConsole.MarkupLine("  [bold cyan]/resume[/]                  Retry the halted step and continue remaining steps");
        AnsiConsole.MarkupLine("  [bold cyan]/recover[/]                 Inject failure context and retry the halted step with agent awareness");
        AnsiConsole.MarkupLine("  [bold cyan]/context[/]                 Show estimated context window usage and per-category breakdown");
        AnsiConsole.MarkupLine("  [bold cyan]/events[/]                  Show session event stats (turns, tool calls, top tools)");
        AnsiConsole.MarkupLine("  [bold cyan]/events stats[/]            Same as /events");
        AnsiConsole.MarkupLine("  [bold cyan]/safe-mode[/]               Show safe mode status");
        AnsiConsole.MarkupLine("  [bold cyan]/safe-mode on[/]            Disable Shell, Git, Http tools to prevent mutations");
        AnsiConsole.MarkupLine("  [bold cyan]/safe-mode off[/]           Restore tool categories");
        AnsiConsole.MarkupLine("  [bold cyan]/provider[/]                Show current provider, model, and API key");
        AnsiConsole.MarkupLine("  [bold cyan]/provider setup[/]          Reconfigure provider, model, and API key");
        AnsiConsole.MarkupLine("  [bold cyan]/memory[/]                  List all stored memories");
        AnsiConsole.MarkupLine("  [bold cyan]/memory show <name>[/]      Show full body of a memory");
        AnsiConsole.MarkupLine("  [bold cyan]/memory delete <name>[/]    Delete a stored memory");
        AnsiConsole.MarkupLine("  [bold cyan]/memory save[/]             Extract and save memories from the current session now");
        AnsiConsole.MarkupLine("  [bold cyan]/max-tokens <n>[/]          Set max output tokens for each response");
        AnsiConsole.MarkupLine("  [bold cyan]/max-tokens reset[/]        Restore provider default max output tokens");
        AnsiConsole.MarkupLine("  [bold cyan]/exit[/]                    Exit the REPL (auto-saves memories)");
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
