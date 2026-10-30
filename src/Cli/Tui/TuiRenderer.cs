using System.Text.Json;
using fuseraft.Infrastructure.Mcp;
using Repl = fuseraft.Cli.Commands.Repl;

namespace fuseraft.Cli.Tui;

/// <summary>A tool's whole result, an approval's whole diff, or the model's thinking, for the output viewer (Ctrl+O).</summary>
internal sealed record ToolOutput(string Title, string Text, bool Diff);

/// <summary>
/// Draws a turn's events in the transcript: the model's Markdown styled as it streams (the open part of the message
/// redrawn as it grows), its thinking under a ∴ while it lasts, one line per tool call that counts up while it runs and
/// turns ✓ or ✗ when it ends, and the files it changed. Everything else in the transcript comes from Spectre, as on a
/// plain terminal.
/// </summary>
internal sealed class TuiRenderer(Transcript transcript, Func<int> width, TimeProvider? clock = null)
{
    /// <summary>How many outputs are kept for the viewer; older ones are let go.</summary>
    public const int KeptOutputs = 100;

    /// <summary>How many rows of the thinking show while it streams: its latest, so it reads as progress.</summary>
    public const int ThinkingRows = 6;

    /// <summary>How long a call runs before its line starts counting.</summary>
    public static readonly TimeSpan Counted = TimeSpan.FromSeconds(2);

    // What a running call was started with, for its output, and its line, to redraw as it runs and when it ends.
    sealed record Call(string Title, string? Arguments, List<Span> Line, string Text, DateTime Started);

    readonly Lock _lock = new();
    readonly List<ToolOutput> _outputs = [];
    readonly Dictionary<string, Call> _calls = [];
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    Markdown? _message; // the model's message being streamed, while it's being redrawn
    System.Text.StringBuilder? _thought; // the model's thinking so far, until it moves on and the thinking is folded
    bool _marked; // whether the message being streamed has its mark yet

    static Theme T => Theme.Current;

    /// <summary>The next piece of the model's reply.</summary>
    public void Text(string delta)
    {
        lock (_lock)
        {
            EndThinking();
            // Something else wrote to the transcript since, which ended the message there; what follows starts a new one.
            if (_message is not null && !transcript.Streaming)
                _message = null;
            if (_message is null)
            {
                if (delta.Trim().Length == 0)
                    return;
                StartLine();
                _message = new Markdown(width());
                _marked = false;
                delta = delta.TrimStart('\n');
            }
            var (settled, open) = _message.Add(delta);
            // Settled lines stay put, so they take the mark for good; the open line is redrawn, so it only borrows it.
            if (!_marked)
            {
                settled = Marked(settled);
                if (!_marked)
                    open = Marked(open, keep: false);
            }
            transcript.Stream(settled, open);
        }
    }

    // The text with the mark before its first line that isn't blank; unchanged while it's all blank.
    string Marked(string text, bool keep = true)
    {
        var start = 0;
        while (start < text.Length && text[start] is '\n' or '\r')
            start++;
        if (start == text.Length)
            return text;
        _marked = keep;
        return text[..start] + Paint(T.Accent, Repl.ReplConsole.ReplyMark) + " " + text[start..];
    }

    /// <summary>The next piece of the model's thinking, shown dimmed under a ∴ until it moves on.</summary>
    public void Thinking(string delta)
    {
        lock (_lock)
        {
            EndMessage();
            if (_thought is null)
                StartLine();
            (_thought ??= new()).Append(delta);
            transcript.Stream("", ThinkingRowsOf(_thought.ToString()));
        }
    }

    /// <summary>Ends the message being streamed; what comes next starts a new message, with its own mark.</summary>
    public void End(bool turn)
    {
        lock (_lock)
        {
            EndThinking();
            EndMessage();
            if (!turn)
                return;
            // Calls a turn didn't see the end of were cancelled with it.
            foreach (var call in _calls.Values)
                transcript.Rewrite(call.Line, call.Text + Paint(T.Muted, " · stopped"));
            _calls.Clear();
        }
    }

    public void ToolStarted(string callId, string name, string summary, string? agent = null, string? arguments = null)
    {
        lock (_lock)
        {
            End(turn: false);
            var text = (agent is null ? "  " : "    " + Paint(T.Agent, $"[{agent}]") + " ") + Paint(T.Tool, "↳ " + name) + (summary.Length == 0 ? "" : Paint(T.Muted, " " + summary));
            var line = Line(text);
            // A subagent's calls report no result here, so only the turn's own are remembered for their output.
            if (agent is null)
                _calls[callId] = new((name + " " + summary).Trim(), arguments, line, text, _clock.GetUtcNow().UtcDateTime);
        }
    }

    /// <summary>Redraws the line of each call that has run for <see cref="Counted"/> with how long it has run.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var call in _calls.Values)
                if (now - call.Started >= Counted)
                    transcript.Rewrite(call.Line, call.Text + Paint(T.Muted, " · " + StatusView.Elapsed(now - call.Started)));
        }
    }

    public void ToolFinished(string callId, string name, bool ok, string result, string? agent = null)
    {
        lock (_lock)
        {
            _calls.Remove(callId, out var call);
            var (title, arguments) = call is null ? (name, null) : (call.Title, call.Arguments);
            if (call is not null)
            {
                var took = _clock.GetUtcNow().UtcDateTime - call.Started;
                transcript.Rewrite(call.Line, call.Text + " " + (ok ? Paint(T.Success, "✓") : Paint(T.Error, "✗"))
                    + (took >= TimeSpan.FromSeconds(1) ? Paint(T.Muted, " " + StatusView.Elapsed(took)) : ""));
            }
            var shown = McpToolResult.Display(result, indentJson: true);
            if (shown.Length == 0)
                shown = "(no output)";
            // The arguments too, so what the call was asked to do can be read beside what it answered.
            var text = string.IsNullOrEmpty(arguments) ? shown : $"Arguments\n{Indent(arguments)}\n\nResult\n{Indent(shown)}";
            Keep(new ToolOutput(ok ? title : title + " (failed)", text, Diff: false));
            if (!ok)
                Line(Paint(T.Error, (agent is null ? "    " : "      ") + FirstLine(McpToolResult.Display(result))));
        }
    }

    public void FileChanged(string path, int? added, int? removed)
    {
        lock (_lock)
        {
            End(turn: false);
            Line(Paint(T.Muted, $"  ✎ {path}") + (added is null ? "" : " " + Paint(T.Success, $"+{added}") + " " + Paint(T.Error, $"-{removed}")));
        }
    }

    /// <summary>Keeps <paramref name="output"/> for the viewer.</summary>
    public void Keep(ToolOutput output)
    {
        lock (_outputs)
        {
            _outputs.Add(output);
            if (_outputs.Count > KeptOutputs)
                _outputs.RemoveAt(0);
        }
    }

    /// <summary>The kept outputs, oldest first.</summary>
    public IReadOnlyList<ToolOutput> Outputs
    {
        get
        {
            lock (_outputs)
                return [.. _outputs];
        }
    }

    /// <summary>
    /// A whole line; while a message or thinking streams, it goes above what's still being written. Returns the line, for
    /// <see cref="Transcript.Rewrite"/>.
    /// </summary>
    public List<Span> Line(string text)
    {
        lock (_lock)
            return (_message ?? (object?)_thought) is not null && transcript.Streaming
                ? transcript.WriteLineAbove(text)
                : transcript.AddLine(text);
    }

    // A stream redraws from the line it starts on, so it has to start on one of its own.
    void StartLine()
    {
        if (transcript.Lines(Math.Max(0, transcript.Count - 1)) is [{ Count: > 0 }])
            transcript.Write("\n");
    }

    /// <summary>A unified diff, colored, for the viewer: + lines in success, - lines in error, hunk headers muted.</summary>
    public static void Diff(Transcript into, string diff)
    {
        foreach (var line in diff.TrimEnd('\n').Split('\n'))
            into.Write("  " + (line.StartsWith('+') ? Paint(T.Success, line) : line.StartsWith('-') ? Paint(T.Error, line) : line.StartsWith("@@") ? Paint(T.Muted, line) : line) + "\n");
    }

    void EndMessage()
    {
        if (_message is null)
            return;
        _message = null;
        transcript.EndStream();
    }

    // Folds the thinking into one line, and keeps it whole for the viewer.
    void EndThinking()
    {
        if (_thought is null)
            return;
        var thought = _thought.ToString().Trim();
        _thought = null;
        transcript.Stream("", "");
        transcript.EndStream();
        if (thought.Length == 0)
            return;
        var words = thought.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Line(Paint(T.Muted, $"  ∴ thought · {words:N0} word{(words == 1 ? "" : "s")} · Ctrl+O"));
        Keep(new ToolOutput("thinking", thought, Diff: false));
    }

    // While it streams, the thinking's latest rows, dimmed under a ∴.
    string ThinkingRowsOf(string thought)
    {
        var columns = Math.Max(width() - 4, 20);
        // Only the end is shown, so only the end is wrapped; the cut is at a line break, so its first row wraps as written.
        var tail = thought.Length > ThinkingRows * columns * 2 ? thought[^(ThinkingRows * columns * 2)..] : thought;
        if (tail.Length < thought.Length && tail.IndexOf('\n') is var cut and >= 0)
            tail = tail[(cut + 1)..];
        var rows = tail.Trim().Split('\n')
            .SelectMany(line => Transcript.Wrap([new Span(line.TrimEnd(), default)], columns))
            .Select(row => string.Concat(row.Select(s => s.Text)))
            .TakeLast(ThinkingRows);
        return string.Join("\n", rows.Select((row, i) => (i == 0 ? "  ∴ " : "    ") + Paint(T.Muted, row)));
    }

    /// <summary>A tool call's arguments in a few words: the one that says most (a command, a query, a path), else the first.</summary>
    public static string Summarize(IDictionary<string, object?>? args)
    {
        if (args is not { Count: > 0 })
            return "";
        // What a search looks for says more than where it looks: "search_content src" hides the query.
        string[] telling = ["command", "query", "pattern", "symbol", "path", "directory", "url", "task", "name", "source"];
        var value = telling.Select(k => args.FirstOrDefault(a => a.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value)
            .FirstOrDefault(v => v is not null) ?? args.First().Value;
        var text = (value?.ToString() ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length > 100 ? text[..97] + "..." : text;
    }

    /// <summary>A call's arguments for the viewer: one per line, string values as they are, multi-line ones beneath their name.</summary>
    public static string FormatArguments(IDictionary<string, object?>? args)
    {
        if (args is not { Count: > 0 })
            return "";
        var lines = new List<string>();
        foreach (var (key, value) in args)
        {
            var text = value switch
            {
                null => "null",
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
                JsonElement e => e.GetRawText(),
                _ => JsonSerializer.Serialize(value),
            };
            lines.Add(text.Contains('\n') ? $"{key}:\n{Indent(text.TrimEnd('\n'))}" : $"{key}: {text}");
        }
        return string.Join("\n", lines);
    }

    static string Indent(string text) => "  " + text.ReplaceLineEndings("\n").Replace("\n", "\n  ");

    static string Paint(string code, string s) => code.Length == 0 || s.Length == 0 ? s : $"\e[{code}m{s}\e[0m";

    static string FirstLine(string s)
    {
        var line = s.Split('\n', 2)[0];
        return line.Length > 200 ? line[..197] + "..." : line;
    }
}
