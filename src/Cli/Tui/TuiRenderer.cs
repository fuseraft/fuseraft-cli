using Repl = fuseraft.Cli.Commands.Repl;

namespace fuseraft.Cli.Tui;

/// <summary>A tool's whole result, an approval's whole diff, or the model's thinking, for the output viewer (Ctrl+O).</summary>
internal sealed record ToolOutput(string Title, string Text, bool Diff);

/// <summary>
/// Draws a turn's events in the transcript: the model's Markdown styled as it streams (the open part of the message
/// redrawn as it grows), its thinking under a ∴ while it lasts, one line per tool call, and the files it changed.
/// Everything else in the transcript comes from Spectre, as on a plain terminal.
/// </summary>
internal sealed class TuiRenderer(Transcript transcript, Func<int> width)
{
    /// <summary>How many outputs are kept for the viewer; older ones are let go.</summary>
    public const int KeptOutputs = 100;

    /// <summary>How many rows of the thinking show while it streams: its latest, so it reads as progress.</summary>
    public const int ThinkingRows = 6;

    readonly Lock _lock = new();
    readonly List<ToolOutput> _outputs = [];
    readonly Dictionary<string, string> _calls = []; // what each running call was started with, for its output's title
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
        }
    }

    public void ToolStarted(string callId, string name, string summary, string? agent = null)
    {
        lock (_lock)
        {
            End(turn: false);
            // A subagent's calls report no result here, so only the turn's own are remembered for their output's title.
            if (agent is null)
                _calls[callId] = (name + " " + summary).Trim();
            Line((agent is null ? "  " : "    " + Paint(T.Agent, $"[{agent}]") + " ") + Paint(T.Tool, "↳ " + name) + (summary.Length == 0 ? "" : Paint(T.Muted, " " + summary)));
        }
    }

    public void ToolFinished(string callId, string name, bool ok, string result, string? agent = null)
    {
        lock (_lock)
        {
            var title = _calls.Remove(callId, out var started) ? started : name;
            Keep(new ToolOutput(ok ? title : title + " (failed)", result.Length == 0 ? "(no output)" : result, Diff: false));
            if (!ok)
                Line(Paint(T.Error, (agent is null ? "    " : "      ") + FirstLine(result)));
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

    /// <summary>A whole line; while a message or thinking streams, it goes above what's still being written.</summary>
    public void Line(string text)
    {
        lock (_lock)
        {
            if ((_message ?? (object?)_thought) is not null && transcript.Streaming)
                transcript.WriteLineAbove(text);
            else
            {
                StartLine();
                transcript.Write(text + "\n");
            }
        }
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

    /// <summary>A tool call's arguments in a few words: the one that says most (a command, a path), else the first.</summary>
    public static string Summarize(IDictionary<string, object?>? args)
    {
        if (args is not { Count: > 0 })
            return "";
        string[] telling = ["command", "path", "directory", "pattern", "query", "url", "symbol", "task", "name", "source"];
        var value = telling.Select(k => args.FirstOrDefault(a => a.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value)
            .FirstOrDefault(v => v is not null) ?? args.First().Value;
        var text = (value?.ToString() ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length > 100 ? text[..97] + "..." : text;
    }

    static string Paint(string code, string s) => code.Length == 0 || s.Length == 0 ? s : $"\e[{code}m{s}\e[0m";

    static string FirstLine(string s)
    {
        var line = s.Split('\n', 2)[0];
        return line.Length > 200 ? line[..197] + "..." : line;
    }
}
