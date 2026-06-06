using System.Text;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Line editor with in-session history. Falls back to Console.ReadLine when stdin
/// is redirected so the REPL stays scriptable.
/// </summary>
internal sealed class ReplLineReader
{
    // ── Tab completion ────────────────────────────────────────────────────────

    private static readonly string[] SlashCommands =
    [
        "/adversarial", "/assist", "/clear", "/compact", "/context",
        "/conversation", "/events", "/execute", "/exit", "/explore",
        "/fork", "/help", "/history", "/last", "/locate",
        "/max-tokens", "/memory", "/model", "/paste", "/plan",
        "/provider", "/recover", "/resume", "/retry", "/rewind",
        "/safe-mode", "/save", "/sessions", "/switch", "/system",
        "/tools",
    ];

    private static readonly Dictionary<string, string[]> SubCommands =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["/adversarial"] = ["off", "on"],
        ["/fork"]        = ["switch"],
        ["/max-tokens"]  = ["reset"],
        ["/memory"]      = ["delete", "list", "save", "show"],
        ["/provider"]    = ["setup"],
        ["/safe-mode"]   = ["off", "on"],
        ["/tools"]       = ["disable", "enable"],
    };

    private bool     _tabActive;
    private int      _tabIndex;
    private string[] _tabMatches  = [];
    private string[] _skillSlugs  = [];

    internal void SetSkillSlugs(string[] slugs) => _skillSlugs = slugs;

    // ── Input history ─────────────────────────────────────────────────────────

    private readonly List<string> _history = [];

    public string? ReadLine()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        var buffer    = new StringBuilder();
        int cursorPos = 0;
        int histIdx   = _history.Count;
        string savedLine = string.Empty;

        int startLeft, startTop;
        try
        {
            startLeft = Console.CursorLeft;
            startTop  = Console.CursorTop;
        }
        catch
        {
            startLeft = 0;
            startTop  = 0;
        }

        int longestWritten = 0;

        void Redraw()
        {
            try { Console.SetCursorPosition(startLeft, startTop); } catch { }
            var content = buffer.ToString();
            var pad     = Math.Max(0, longestWritten - content.Length);
            Console.Write(content);
            if (pad > 0) Console.Write(new string(' ', pad));
            longestWritten = Math.Max(longestWritten, content.Length);

            // After writing, detect and absorb any terminal scroll. Writing
            // near the bottom of the viewport causes the terminal to scroll up,
            // shifting startTop. Detect this by comparing where the cursor
            // *should* be (last written char) with where it actually landed.
            //
            // Use longestWritten-1 (index of the last written char) not
            // longestWritten (index after it): terminals enter "pending-wrap"
            // state when the cursor reaches the last column, so CursorTop stays
            // on the current row. Using longestWritten would falsely predict
            // row+1 whenever input exactly fills a line width, fire a phantom
            // scroll-of-1, and wrongly decrement startTop.
            if (!Console.IsOutputRedirected && longestWritten > 0)
            {
                try
                {
                    var width = Math.Max(Console.WindowWidth, 1);
                    var expectedEndRow = startTop + (startLeft + longestWritten - 1) / width;
                    var scrolled = expectedEndRow - Console.CursorTop;
                    if (scrolled > 0) startTop = Math.Max(0, startTop - scrolled);
                }
                catch { }
            }

            MoveTo(cursorPos);
        }

        void MoveTo(int pos)
        {
            var width = Console.IsOutputRedirected ? 80 : Math.Max(Console.WindowWidth, 1);
            var abs   = startLeft + pos;
            try { Console.SetCursorPosition(abs % width, startTop + abs / width); } catch { }
        }

        try
        {
            while (true)
            {
                ConsoleKeyInfo info;
                try { info = Console.ReadKey(intercept: true); }
                catch (InvalidOperationException) { return null; }

                // Any key other than Tab breaks the current tab-cycling run.
                if (info.Key != ConsoleKey.Tab)
                    _tabActive = false;

                switch (info.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        var line = buffer.ToString();
                        if (!string.IsNullOrEmpty(line))
                        {
                            // Avoid consecutive duplicate entries.
                            if (_history.Count == 0 || _history[^1] != line)
                                _history.Add(line);
                        }
                        return line;

                    case ConsoleKey.C when info.Modifiers.HasFlag(ConsoleModifiers.Control):
                        Console.WriteLine("^C");
                        return null;

                    case ConsoleKey.D when info.Modifiers.HasFlag(ConsoleModifiers.Control):
                        if (buffer.Length == 0) { Console.WriteLine(); return null; }
                        // Ctrl+D with text: delete char under cursor (same as Delete).
                        if (cursorPos < buffer.Length) { buffer.Remove(cursorPos, 1); Redraw(); }
                        break;

                    // ── History navigation ────────────────────────────────────
                    case ConsoleKey.UpArrow:
                        if (histIdx > 0)
                        {
                            if (histIdx == _history.Count) savedLine = buffer.ToString();
                            histIdx--;
                            buffer.Clear();
                            buffer.Append(_history[histIdx]);
                            cursorPos = buffer.Length;
                            Redraw();
                        }
                        break;

                    case ConsoleKey.DownArrow:
                        if (histIdx < _history.Count)
                        {
                            histIdx++;
                            var next = histIdx == _history.Count ? savedLine : _history[histIdx];
                            buffer.Clear();
                            buffer.Append(next);
                            cursorPos = buffer.Length;
                            Redraw();
                        }
                        break;

                    // ── Cursor movement ───────────────────────────────────────
                    case ConsoleKey.LeftArrow:
                        if (info.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            while (cursorPos > 0 && buffer[cursorPos - 1] == ' ') cursorPos--;
                            while (cursorPos > 0 && buffer[cursorPos - 1] != ' ') cursorPos--;
                            MoveTo(cursorPos);
                        }
                        else if (cursorPos > 0) { cursorPos--; MoveTo(cursorPos); }
                        break;

                    case ConsoleKey.RightArrow:
                        if (info.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            while (cursorPos < buffer.Length && buffer[cursorPos] == ' ') cursorPos++;
                            while (cursorPos < buffer.Length && buffer[cursorPos] != ' ') cursorPos++;
                            MoveTo(cursorPos);
                        }
                        else if (cursorPos < buffer.Length) { cursorPos++; MoveTo(cursorPos); }
                        break;

                    case ConsoleKey.Home:
                    case ConsoleKey.A when info.Modifiers.HasFlag(ConsoleModifiers.Control):
                        cursorPos = 0;
                        MoveTo(0);
                        break;

                    case ConsoleKey.End:
                    case ConsoleKey.E when info.Modifiers.HasFlag(ConsoleModifiers.Control):
                        cursorPos = buffer.Length;
                        MoveTo(cursorPos);
                        break;

                    // ── Deletion ──────────────────────────────────────────────
                    case ConsoleKey.Backspace:
                        if (cursorPos > 0) { buffer.Remove(cursorPos - 1, 1); cursorPos--; Redraw(); }
                        break;

                    case ConsoleKey.Delete:
                        if (cursorPos < buffer.Length) { buffer.Remove(cursorPos, 1); Redraw(); }
                        break;

                    case ConsoleKey.U when info.Modifiers.HasFlag(ConsoleModifiers.Control):
                        if (cursorPos > 0) { buffer.Remove(0, cursorPos); cursorPos = 0; Redraw(); }
                        break;

                    case ConsoleKey.K when info.Modifiers.HasFlag(ConsoleModifiers.Control):
                        if (cursorPos < buffer.Length)
                        {
                            buffer.Remove(cursorPos, buffer.Length - cursorPos);
                            Redraw();
                        }
                        break;

                    case ConsoleKey.W when info.Modifiers.HasFlag(ConsoleModifiers.Control):
                        if (cursorPos > 0)
                        {
                            var end = cursorPos;
                            while (cursorPos > 0 && buffer[cursorPos - 1] == ' ') cursorPos--;
                            while (cursorPos > 0 && buffer[cursorPos - 1] != ' ') cursorPos--;
                            buffer.Remove(cursorPos, end - cursorPos);
                            Redraw();
                        }
                        break;

                    // ── Tab completion ────────────────────────────────────────
                    case ConsoleKey.Tab:
                    {
                        var text = buffer.ToString();

                        if (text.StartsWith('$') && !text.Contains(' '))
                        {
                            // Complete $skill-name
                            var partial = text[1..];
                            if (!_tabActive)
                            {
                                _tabMatches = _skillSlugs
                                    .Where(s => s.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                                    .Select(s => '$' + s)
                                    .ToArray();
                                _tabIndex = -1;
                            }
                            if (_tabMatches.Length == 0) break;
                            _tabIndex = (_tabIndex + 1) % _tabMatches.Length;
                            buffer.Clear();
                            buffer.Append(_tabMatches[_tabIndex]);
                            if (_tabMatches.Length == 1) buffer.Append(' ');
                            cursorPos  = buffer.Length;
                            _tabActive = true;
                            Redraw();
                            break;
                        }

                        if (!text.StartsWith('/')) break;

                        var spaceIdx = text.IndexOf(' ');
                        if (spaceIdx < 0)
                        {
                            // Complete the command word.
                            if (!_tabActive)
                            {
                                _tabMatches = SlashCommands
                                    .Where(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                                    .ToArray();
                                _tabIndex = -1;
                            }
                            if (_tabMatches.Length == 0) break;
                            _tabIndex = (_tabIndex + 1) % _tabMatches.Length;
                            buffer.Clear();
                            buffer.Append(_tabMatches[_tabIndex]);
                            if (_tabMatches.Length == 1) buffer.Append(' ');
                        }
                        else
                        {
                            // Complete the subcommand word.
                            var cmd     = text[..spaceIdx];
                            var partial = text[(spaceIdx + 1)..];
                            if (!SubCommands.TryGetValue(cmd, out var subs)) break;
                            if (!_tabActive)
                            {
                                _tabMatches = subs
                                    .Where(s => s.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                                    .ToArray();
                                _tabIndex = -1;
                            }
                            if (_tabMatches.Length == 0) break;
                            _tabIndex = (_tabIndex + 1) % _tabMatches.Length;
                            buffer.Clear();
                            buffer.Append(cmd);
                            buffer.Append(' ');
                            buffer.Append(_tabMatches[_tabIndex]);
                            if (_tabMatches.Length == 1) buffer.Append(' ');
                        }

                        cursorPos  = buffer.Length;
                        _tabActive = true;
                        Redraw();
                        break;
                    }

                    // ── Character insert ──────────────────────────────────────
                    default:
                        if (info.KeyChar != '\0' && !char.IsControl(info.KeyChar))
                        {
                            buffer.Insert(cursorPos, info.KeyChar);
                            cursorPos++;
                            Redraw();
                        }
                        break;
                }
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
