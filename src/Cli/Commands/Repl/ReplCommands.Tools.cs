using System.Text.Json;
using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Repl;

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /tools
    // -------------------------------------------------------------------------

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
                await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/tools disable", category = match });
            }
            else
            {
                ctx.DisabledCategories.Remove(match);
                ctx.ChatOptions = ctx.BuildChatOptions();
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(match)} tools enabled.[/]");
                await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/tools enable", category = match });
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

    // -------------------------------------------------------------------------
    // /safe-mode
    // -------------------------------------------------------------------------

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
                await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/safe-mode on" });
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
                await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/safe-mode off" });
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

    // -------------------------------------------------------------------------
    // /adversarial
    // -------------------------------------------------------------------------

    private static CommandResult CmdAdversarial(ReplSessionContext ctx, string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            AnsiConsole.MarkupLine(ctx.AdversarialMode
                ? "[dim]Adversarial mode:[/] [green]on[/]  [dim](critic agent reviews every /execute step and free-form response)[/]"
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
            AnsiConsole.MarkupLine("[dim]Adversarial mode[/] [green]on[/][dim]: critic agent will review every /execute step and free-form response.[/]");
            _ = ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/adversarial on" });
        }
        else if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            ctx.AdversarialMode = false;
            AnsiConsole.MarkupLine("[dim]Adversarial mode[/] [dim]off[/][dim].[/]");
            _ = ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/adversarial off" });
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Unknown /adversarial argument:[/] {Markup.Escape(arg)}");
            AnsiConsole.MarkupLine("[dim]Usage: /adversarial     — show current status[/]");
            AnsiConsole.MarkupLine("[dim]       /adversarial on  — enable critic agent for /execute steps and free-form responses[/]");
            AnsiConsole.MarkupLine("[dim]       /adversarial off — disable critic agent[/]");
        }
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /memory
    // -------------------------------------------------------------------------

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
                {
                    if (ctx.JsonMode)
                        ReplJsonBridge.Emit(new { type = "text", text = $"No memory named '{memArg}'." });
                    else
                        AnsiConsole.MarkupLine($"[yellow]No memory named '{Markup.Escape(memArg)}'.[/]");
                }
                else if (ctx.JsonMode)
                {
                    ReplJsonBridge.Emit(new { type = "text", text =
                        $"**{found.Name}** ({found.Type})\n{found.Description}\n\n{found.Body}" });
                }
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
                await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/memory delete", name = memArg });
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
                    await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new
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

    // -------------------------------------------------------------------------
    // /events
    // -------------------------------------------------------------------------

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

        var lines        = await File.ReadAllLinesAsync(ctx.EventsPath);
        var turnSet      = new SortedSet<int>();
        var toolCounts   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var toolsByTurn  = new SortedDictionary<int, List<string>>();
        var tokensByTurn = new SortedDictionary<int, (long Input, long Output)>();
        var totalTools   = 0;
        var totalTurns   = 0;
        long totalInputTokens = 0, totalOutputTokens = 0;

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

                if (et == EventTypes.TurnEnd &&
                    root.TryGetProperty("payload", out var tp) &&
                    root.TryGetProperty("turn", out var tEl3) && tEl3.ValueKind == JsonValueKind.Number)
                {
                    var inTok  = tp.TryGetProperty("input_tokens",  out var itEl) && itEl.ValueKind == JsonValueKind.Number ? itEl.GetInt64() : 0;
                    var outTok = tp.TryGetProperty("output_tokens", out var otEl) && otEl.ValueKind == JsonValueKind.Number ? otEl.GetInt64() : 0;
                    if (inTok > 0 || outTok > 0)
                    {
                        tokensByTurn[tEl3.GetInt32()] = (inTok, outTok);
                        totalInputTokens  += inTok;
                        totalOutputTokens += outTok;
                    }
                }
            }
            catch { /* skip malformed lines */ }
        }

        foreach (var t in turnSet)
            if (!toolsByTurn.ContainsKey(t)) toolsByTurn[t] = [];

        AnsiConsole.MarkupLine($"  [dim]Session:[/]     {Markup.Escape(ctx.SessionId)}");
        AnsiConsole.MarkupLine($"  [dim]Turns:[/]       {totalTurns}");
        AnsiConsole.MarkupLine($"  [dim]Tool calls:[/]  {totalTools}");
        if (totalInputTokens > 0 || totalOutputTokens > 0)
            AnsiConsole.MarkupLine($"  [dim]Tokens:[/]      {totalInputTokens:N0} in / {totalOutputTokens:N0} out  [dim](actual)[/]");

        if (toolsByTurn.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("  [dim]Per-turn breakdown:[/]");
            foreach (var (turn, tlist) in toolsByTurn)
            {
                var label   = turn >= 0 ? $"turn {turn}" : "unknown";
                var tokSuffix = tokensByTurn.TryGetValue(turn, out var tok)
                    ? $"  [dim]· {tok.Input:N0} in / {tok.Output:N0} out[/]"
                    : string.Empty;
                if (tlist.Count == 0)
                {
                    AnsiConsole.MarkupLine($"    [dim]{label}  (no tool calls)[/]{tokSuffix}");
                }
                else
                {
                    AnsiConsole.MarkupLine($"    [dim]{label}  ({tlist.Count} call{(tlist.Count == 1 ? "" : "s")}):[/]{tokSuffix}");
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

        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/events stats" });
    }
}
