using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace fuseraft.Cli.Tui;

/// <summary>
/// Copies text for /copy, and reads it for Ctrl+V. The terminal is asked through OSC 52, which reaches the user's own
/// clipboard even over SSH; on a local session, the system's clipboard tool is used too, since not every terminal takes OSC 52.
/// </summary>
internal static class Clipboard
{
    /// <summary>The OSC 52 sequence that asks the terminal to put <paramref name="text"/> on the clipboard.</summary>
    public static string Osc52(string text) => $"\e]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\a";

    /// <summary>Copies <paramref name="text"/> with the system's clipboard tool; returns the tool's name, or null when none worked.</summary>
    public static string? CopyNatively(string text)
    {
        if (Environment.GetEnvironmentVariable("SSH_CONNECTION") is not null)
            return null; // the clipboard there is the server's, not the user's
        foreach (var (program, args) in Tools())
            try
            {
                using var process = Process.Start(new ProcessStartInfo(program, args)
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                })!;
                process.StandardInput.Write(text);
                process.StandardInput.Close();
                if (process.WaitForExit(2000) && process.ExitCode == 0)
                    return program;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
            }
        return null;
    }

    /// <summary>The system clipboard's text, or null when it's empty or can't be read (no tool, or an SSH session).</summary>
    public static string? ReadNatively()
    {
        if (Environment.GetEnvironmentVariable("SSH_CONNECTION") is not null)
            return null;
        if (OperatingSystem.IsWindows())
            return Win32.ReadText();
        foreach (var (program, args) in ReadTools())
            try
            {
                using var process = Process.Start(new ProcessStartInfo(program, args)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                })!;
                var output = process.StandardOutput.ReadToEndAsync();
                if (process.WaitForExit(2000) && process.ExitCode == 0)
                    return output.GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
            }
        return null;
    }

    /// <summary>Clipboard text for a one-line field: line breaks become spaces, and the ends are trimmed.</summary>
    public static string SingleLine(string text) => text.ReplaceLineEndings(" ").Trim();

    static IEnumerable<(string, string[])> ReadTools()
    {
        if (OperatingSystem.IsMacOS())
            yield return ("pbpaste", []);
        else
        {
            if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null)
                yield return ("wl-paste", ["--no-newline"]);
            if (Environment.GetEnvironmentVariable("DISPLAY") is not null)
            {
                yield return ("xclip", ["-selection", "clipboard", "-o"]);
                yield return ("xsel", ["--clipboard", "--output"]);
            }
        }
    }

    // Read through the Win32 API rather than powershell's Get-Clipboard, which takes most of a second to start.
    static class Win32
    {
        const uint UnicodeText = 13;

        public static string? ReadText()
        {
            // Another program can hold the clipboard for a moment.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero))
                    try
                    {
                        var handle = GetClipboardData(UnicodeText);
                        if (handle == IntPtr.Zero)
                            return null;
                        var data = GlobalLock(handle);
                        if (data == IntPtr.Zero)
                            return null;
                        try { return Marshal.PtrToStringUni(data); }
                        finally { GlobalUnlock(handle); }
                    }
                    finally { CloseClipboard(); }
                Thread.Sleep(20);
            }
            return null;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetClipboardData(uint format);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalUnlock(IntPtr memory);
    }

    static IEnumerable<(string, string[])> Tools()
    {
        if (OperatingSystem.IsMacOS())
            yield return ("pbcopy", []);
        else if (OperatingSystem.IsWindows())
            yield return ("clip.exe", []);
        else
        {
            if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null)
                yield return ("wl-copy", []);
            if (Environment.GetEnvironmentVariable("DISPLAY") is not null)
            {
                yield return ("xclip", ["-selection", "clipboard"]);
                yield return ("xsel", ["--clipboard", "--input"]);
            }
        }
    }
}
