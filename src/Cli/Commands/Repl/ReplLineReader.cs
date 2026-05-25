using System.Text;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Line editor with in-session history. Falls back to Console.ReadLine when stdin
/// is redirected so the REPL stays scriptable.
/// </summary>
internal sealed class ReplLineReader
{
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

            // After writing, detect whether the terminal scrolled. If the
            // content (or padding) pushed the cursor past the last row the
            // terminal scrolls up and startTop becomes stale — the next
            // SetCursorPosition call lands in the middle of the wrapped
            // content instead of at the beginning, causing the buffer to be
            // written again from that mid-line position (the duplication bug).
            //
            // Fix: compute where the cursor *should* be based on the number of
            // characters written. If Console.CursorTop is less than that, the
            // difference is how many rows were scrolled away; subtract that
            // from startTop so the next Redraw anchors correctly.
            if (!Console.IsOutputRedirected)
            {
                try
                {
                    var width = Math.Max(Console.WindowWidth, 1);
                    // longestWritten == total chars on screen (content + any pad).
                    var expectedEndRow = startTop + (startLeft + longestWritten) / width;
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
