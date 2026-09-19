using System.Text;
using System.Text.Json;
using fuseraft.Cli.Display;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Re-displays a session's most recent turns from <c>ctx.History</c> — used when a saved session is
/// restored (<c>--resume</c>, <c>/switch</c>) and on demand via <c>/replay</c>. Terminal output
/// mirrors how a live turn renders; the VS Code bridge gets one <c>replay</c> event that the
/// webview turns into ordinary chat bubbles.
/// </summary>
internal static class ReplReplay
{
    internal const int DefaultTurns = 3;

    // A pasted log or file in one message would otherwise bury the replay; the model still has the
    // full text in history, this only bounds what is re-displayed.
    private const int MaxUserChars = 2000;
    private const int MaxArgChars  = 2000;

    internal sealed record ReplayToolCall(string Name, IDictionary<string, object?>? Args);
    internal sealed record ReplayTurn(string User, string Assistant, IReadOnlyList<ReplayToolCall> ToolCalls);
    internal sealed record ReplayTranscript(IReadOnlyList<ReplayTurn> Turns, bool Compacted);

    // -------------------------------------------------------------------------
    // Transcript extraction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Groups history into user-visible turns: one real user message plus everything the model did
    /// in answer to it (all assistant text across tool rounds and self-correction rounds, and the
    /// tool calls it made). Tool-result messages are dropped; internal user-role messages never
    /// become turns of their own.
    /// </summary>
    internal static ReplayTranscript BuildTranscript(IReadOnlyList<ChatMessage> history)
    {
        var turns     = new List<ReplayTurn>();
        var compacted = false;

        // null user = no open turn; assistant content seen in that state (the canned reply that
        // follows a /run result, or output orphaned by compaction) belongs to no visible turn.
        string? user = null;
        var text     = new List<string>();
        var tools    = new List<ReplayToolCall>();

        void Close()
        {
            if (user is not null)
            {
                var combined = ReplTurn.SanitizeAssistantResponse(string.Join("\n\n", text), out _).Trim();
                turns.Add(new ReplayTurn(user, combined, tools.ToArray()));
            }
            user = null;
            text.Clear();
            tools.Clear();
        }

        foreach (var m in history)
        {
            if (m.Role == ChatRole.System) continue;

            if (m.Role == ChatRole.User)
            {
                var t = m.Text ?? string.Empty;

                if (t.StartsWith(ReplCommands.CompactedContextPrefix, StringComparison.Ordinal))
                {
                    compacted = true;
                    Close();
                }
                else if (IsStepMessage(t) || t.StartsWith(ReplCommands.RunResultPrefix, StringComparison.Ordinal))
                {
                    Close();
                }
                else if (ReplTurn.IsInternalCorrectionMessage(t))
                {
                    // Continues the turn it corrects — its assistant output is part of that answer.
                }
                else
                {
                    Close();
                    user = t;
                }
                continue;
            }

            if (user is null || m.Role != ChatRole.Assistant) continue;

            var msgText = m.Text;
            if (!string.IsNullOrWhiteSpace(msgText)) text.Add(msgText.Trim());
            foreach (var c in m.Contents)
                if (c is FunctionCallContent fc)
                    tools.Add(new ReplayToolCall(fc.Name, fc.Arguments));
        }
        Close();

        return new ReplayTranscript(turns, compacted);
    }

    // Covers both the "[Step N of M complete]" and "[Step N of M complete — findings below]"
    // forms ExecuteAsync leaves behind for /execute steps.
    private static bool IsStepMessage(string t) =>
        t.StartsWith("[Step ", StringComparison.Ordinal) &&
        t.Contains(" complete", StringComparison.Ordinal);

    // -------------------------------------------------------------------------
    // Entry points
    // -------------------------------------------------------------------------

    internal static int ConfiguredTurns(ReplSessionContext ctx) =>
        ctx.UserCfg?.Repl?.ResumeReplayTurns ?? DefaultTurns;

    /// <summary>Replays the configured number of recent turns; no-op when disabled (0) or empty.</summary>
    internal static void ShowOnRestore(ReplSessionContext ctx)
    {
        var count = ConfiguredTurns(ctx);
        if (count > 0) Show(ctx, count);
    }

    /// <summary>Displays the last <paramref name="count"/> turns. Returns false when there was nothing to show.</summary>
    internal static bool Show(ReplSessionContext ctx, int count)
    {
        var transcript = BuildTranscript(ctx.History);
        var total      = transcript.Turns.Count;
        if (total == 0 || count <= 0) return false;

        var shown = count >= total
            ? transcript.Turns
            : [.. transcript.Turns.Skip(total - count)];

        if (ctx.JsonMode)
            EmitJson(shown, total, transcript.Compacted);
        else
            Print(shown, total, transcript.Compacted);
        return true;
    }

    // -------------------------------------------------------------------------
    // Terminal rendering
    // -------------------------------------------------------------------------

    private static void Print(IReadOnlyList<ReplayTurn> shown, int total, bool compacted)
    {
        var scope = shown.Count == total
            ? $"{total} turn{(total == 1 ? "" : "s")}"
            : $"last {shown.Count} of {total} turns";
        var note = compacted ? " · earlier context was compacted" : string.Empty;

        AnsiConsole.Write(new Rule($"[dim]previous turns — {scope}{note}[/]") { Justification = Justify.Left }
            .RuleStyle(Style.Parse("grey")));

        foreach (var turn in shown)
        {
            AnsiConsole.WriteLine();
            var lines = Truncate(turn.User, MaxUserChars).Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++)
                AnsiConsole.MarkupLine(i == 0
                    ? $"[bold cyan]>[/] {Markup.Escape(lines[i])}"
                    : $"  {Markup.Escape(lines[i])}");

            if (turn.ToolCalls.Count > 0)
                AnsiConsole.MarkupLine($"[dim]  ↳ {Markup.Escape(SummarizeTools(turn.ToolCalls))}[/]");

            if (turn.Assistant.Length > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[dim]fuseraft agent:[/]");
                // Rows terminates its last line; a bare single-block render (a Markup) doesn't, which
                // left the next turn's ">" glued to the end of this reply.
                AnsiConsole.Write(new Rows(MarkdownRenderer.Render(turn.Assistant)));
            }
            else if (turn.ToolCalls.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]  (no response recorded — the turn was interrupted)[/]");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[dim]end of previous turns[/]") { Justification = Justify.Left }
            .RuleStyle(Style.Parse("grey")));
        AnsiConsole.WriteLine();
    }

    /// <summary>"4 tool calls: read_file ×3, patch_file" — names in first-use order.</summary>
    internal static string SummarizeTools(IReadOnlyList<ReplayToolCall> calls)
    {
        var grouped = calls
            .GroupBy(c => c.Name)
            .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
        return $"{calls.Count} tool call{(calls.Count == 1 ? "" : "s")}: {string.Join(", ", grouped)}";
    }

    // -------------------------------------------------------------------------
    // VS Code bridge
    // -------------------------------------------------------------------------

    private static void EmitJson(IReadOnlyList<ReplayTurn> shown, int total, bool compacted) =>
        ReplJsonBridge.Emit(new
        {
            type = "replay",
            total,
            compacted,
            turns = shown.Select(t => new
            {
                user      = Truncate(t.User, MaxUserChars),
                assistant = t.Assistant,
                toolCalls = t.ToolCalls.Select(c => new { name = c.Name, args = TrimArgs(c.Args) }).ToArray(),
            }).ToArray(),
        });

    // Tool arguments can carry whole file bodies (write_file, patch_file); the webview only shows
    // them on hover/expand, so cap each string value instead of shipping megabytes per replay.
    private static Dictionary<string, object?>? TrimArgs(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return null;

        var trimmed = new Dictionary<string, object?>(args.Count);
        foreach (var (key, value) in args)
        {
            trimmed[key] = value switch
            {
                string s => Truncate(s, MaxArgChars),
                JsonElement { ValueKind: JsonValueKind.String } e => Truncate(e.GetString() ?? string.Empty, MaxArgChars),
                _ => value,
            };
        }
        return trimmed;
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        // Don't cut between the halves of a surrogate pair — the JSON serializer rejects a lone one.
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return $"{s[..cut]}… ({s.Length - cut:N0} more chars)";
    }
}
