using System.Diagnostics;
using System.Runtime.InteropServices;
using Terminal.Gui.Drivers;

namespace fuseraft.Cli.Tui;

// Ctrl+G: the draft in $VISUAL or $EDITOR, which gets the terminal until it exits.
internal sealed partial class ReplTui
{
    // Runs on the UI thread, which stops drawing while the editor has the screen; the turn, if one runs, carries on.
    void EditDraft()
    {
        if (OperatingSystem.IsWindows())
        {
            _status.Flash("Ctrl+G needs a Unix terminal; on Windows, use Alt+Enter for new lines");
            return;
        }
        var editor = new[] { "VISUAL", "EDITOR" }.Select(Environment.GetEnvironmentVariable).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? "vi";
        var file = Path.Combine(Path.GetTempPath(), $"fuseraft-draft-{Environment.ProcessId}.md");
        try
        {
            File.WriteAllText(file, _prompt.Text);
            int exit;
            using (TerminalHandoff.Begin())
                exit = RunEditor(editor, file);
            if (exit == 0)
                _prompt.Replace(File.ReadAllText(file).ReplaceLineEndings("\n").TrimEnd('\n'));
            else
                _status.Flash($"{editor} exited with {exit}, so the draft is as it was");
        }
        catch (Exception e)
        {
            _status.Flash($"Couldn't open {editor}: {e.Message}");
        }
        finally
        {
            File.Delete(file);
            _app.LayoutAndDraw(forceRedraw: true);
        }
    }

    // Through the shell, since $EDITOR may carry arguments ("code --wait"); its input is the terminal itself, as the
    // process's own stdin is handed elsewhere while it runs.
    static int RunEditor(string editor, string file)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", editor + " \"$1\" < /dev/tty", "sh", file]))!;
        process.WaitForExit();
        return process.ExitCode;
    }
}

/// <summary>
/// Gives the terminal to another program and takes it back: the main screen in its usual mode, without mouse reports or
/// bracketed paste, and stdin pointed at an empty pipe so Terminal.Gui's reader can't take the program's keys.
/// </summary>
static class TerminalHandoff
{
    public static IDisposable Begin()
    {
        var stdin = dup(0);
        var empty = new int[2];
        if (stdin < 0 || pipe(empty) != 0 || dup2(empty[0], 0) < 0)
            throw new IOException("the terminal couldn't be handed over");
        Write(EscSeqUtils.CSI_DisableMouseEvents + EscSeqUtils.CSI_DisableBracketedPaste
            + EscSeqUtils.CSI_RestoreCursorAndRestoreAltBufferWithBackscroll + EscSeqUtils.CSI_ShowCursor);
        var mode = Stty("-g").Trim();
        Stty("sane");
        return new Handback(() =>
        {
            if (mode.Length > 0)
                Stty(mode);
            Write(EscSeqUtils.CSI_SaveCursorAndActivateAltBufferNoBackscroll + EscSeqUtils.CSI_EnableMouseEvents + EscSeqUtils.CSI_EnableBracketedPaste);
            dup2(stdin, 0);
            close(stdin);
            close(empty[0]);
            close(empty[1]);
        });
    }

    static void Write(string sequence)
    {
        Console.Out.Write(sequence);
        Console.Out.Flush();
    }

    static string Stty(string args)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", $"stty {args} < /dev/tty"]) { RedirectStandardOutput = true })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    sealed class Handback(Action back) : IDisposable
    {
        public void Dispose() => back();
    }

    [DllImport("libc", SetLastError = true)]
    static extern int dup(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern int dup2(int fd, int to);

    [DllImport("libc", SetLastError = true)]
    static extern int pipe(int[] fds);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);
}
