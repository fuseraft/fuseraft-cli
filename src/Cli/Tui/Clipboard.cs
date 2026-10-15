using System.Diagnostics;
using System.Text;

namespace fuseraft.Cli.Tui;

/// <summary>
/// Copies text for /copy. The terminal is asked through OSC 52, which reaches the user's own clipboard even over SSH;
/// on a local session, the system's clipboard tool is used too, since not every terminal takes OSC 52.
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
