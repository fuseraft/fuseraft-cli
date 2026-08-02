using System.Text;
using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Cli.Display;
using fuseraft.Core.Models;

namespace fuseraft.Cli.Commands.Repl;

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /clear
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
        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/clear" });
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /system
    // -------------------------------------------------------------------------

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
            _ = ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/system", prompt = arg });
        }
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /paste
    // -------------------------------------------------------------------------

    private static CommandResult CmdPaste(bool jsonMode)
    {
        if (jsonMode)
        {
            // Paste mode reads raw stdin lines which would corrupt the JSONL bridge.
            // The VS Code panel textarea already supports Shift+Enter for multi-line input.
            ReplJsonBridge.Emit(new { type = "text", text = "Paste mode is not available in the VS Code panel.\n\nUse **Shift+Enter** in the input box to enter multi-line messages." });
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

    // -------------------------------------------------------------------------
    // /save
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdSaveAsync(ReplSessionContext ctx, string arg)
    {
        var path = string.IsNullOrWhiteSpace(arg)
            ? Path.Combine(ctx.Cwd, $"repl-{ctx.SessionId}.md")
            : arg;
        SaveTranscript(ctx.History, ctx.ModelId, path);
        AnsiConsole.MarkupLine($"[dim]Transcript saved to[/] {Markup.Escape(path)}");
        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/save", path });
        return CommandResult.Continue;
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

    // -------------------------------------------------------------------------
    // /history
    // -------------------------------------------------------------------------

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
            var sb = new StringBuilder();
            sb.AppendLine($"## History ({turns.Count} message{(turns.Count == 1 ? "" : "s")})\n");
            foreach (var m in turns)
            {
                var preview = (m.Text ?? string.Empty).Replace('\n', ' ').Trim();
                if (preview.Length > 120) preview = preview[..120] + "…";
                var label = m.Role == ChatRole.User ? "**You**" : "**Assistant**";
                sb.AppendLine($"- {label}: {preview}");
            }
            ReplJsonBridge.Emit(new { type = "text", text = sb.ToString() });
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

    // -------------------------------------------------------------------------
    // /conversation
    // -------------------------------------------------------------------------

    private static void CmdConversation(ReplSessionContext ctx)
    {
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
                ReplJsonBridge.Emit(new { type = "text", text = "No conversation yet." });
            else
                AnsiConsole.MarkupLine("[dim]No conversation yet.[/]");
            return;
        }

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
            ReplJsonBridge.Emit(new { type = "text", text = sb.ToString() });
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

    // -------------------------------------------------------------------------
    // /rewind
    // -------------------------------------------------------------------------

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

        targetTurn = Math.Clamp(targetTurn, 0, totalTurns);

        if (targetTurn == totalTurns)
        {
            AnsiConsole.MarkupLine($"[dim]Already at turn {totalTurns} — nothing to rewind.[/]");
            return CommandResult.Continue;
        }

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
            ReplJsonBridge.Emit(new { type = "text", text = targetTurn == 0
                ? $"## Rewound to Start\n\nAll {removed} turn{(removed == 1 ? "" : "s")} removed."
                : $"## Rewound\n\nNow at turn {targetTurn}. {removed} turn{(removed == 1 ? "" : "s")} removed." });
        }
        else
        {
            AnsiConsole.MarkupLine(targetTurn == 0
                ? $"[dim]Rewound to start — {removed} turn{(removed == 1 ? "" : "s")} removed.[/]"
                : $"[dim]Rewound to after turn {targetTurn} — {removed} turn{(removed == 1 ? "" : "s")} removed.[/]");
        }

        await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new
            { command = "/rewind", target = targetTurn, removed, total_was = totalTurns });
        return CommandResult.Continue;
    }

    // -------------------------------------------------------------------------
    // /retry
    // -------------------------------------------------------------------------

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
            ReplJsonBridge.Emit(new { type = "text", text = $"Retrying: {lastUserText.Replace('\n', ' ').Trim()[..Math.Min(80, lastUserText.Length)]}…" });
        else
            AnsiConsole.MarkupLine("[dim]Retrying last message…[/]");

        _ = ctx.Emitter.EmitAsync(EventTypes.Command, payload: new { command = "/retry" });
        return CommandResult.Send(lastUserText);
    }

    // -------------------------------------------------------------------------
    // /last
    // -------------------------------------------------------------------------

    private static void CmdLast(ReplSessionContext ctx)
    {
        var lastAsst = ctx.History.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastAsst is null)
        {
            if (ctx.JsonMode)
                ReplJsonBridge.Emit(new { type = "text", text = "No assistant response yet." });
            else
                AnsiConsole.MarkupLine("[dim]No assistant response yet.[/]");
            return;
        }

        var text = lastAsst.Text ?? string.Empty;

        if (ctx.JsonMode)
        {
            ReplJsonBridge.Emit(new { type = "text", text });
            return;
        }

        AnsiConsole.MarkupLine("[dim]assistant (last response):[/]");
        AnsiConsole.Write(MarkdownRenderer.Render(text));
        AnsiConsole.WriteLine();
    }

    // -------------------------------------------------------------------------
    // Shared predicate
    // -------------------------------------------------------------------------

    private static bool IsStepSummary(ChatMessage m) =>
        m.Role == ChatRole.User &&
        m.Text is { } t &&
        t.StartsWith("[Step ", StringComparison.Ordinal) &&
        t.Contains(" complete]", StringComparison.Ordinal);
}
