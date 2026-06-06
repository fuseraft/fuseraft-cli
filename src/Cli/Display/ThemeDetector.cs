using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli.Help;

namespace fuseraft.Cli.Display;

/// <summary>
/// Detects whether the terminal is running on a light background so that
/// colours can be adjusted for readability.
/// </summary>
public static class ThemeDetector
{
    private static readonly Lazy<bool> _isLight =
        new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsLightBackground => _isLight.Value;

    // Semantic markup colour strings — use these instead of hard-coding "yellow".
    public static string Warning => IsLightBackground ? "olive"  : "yellow";
    public static string Human   => IsLightBackground ? "black"  : "white";

    /// <summary>
    /// Returns a light-mode <see cref="HelpProviderStyle"/> when a light terminal
    /// background is detected, otherwise <c>null</c> (use Spectre's defaults).
    /// </summary>
    public static HelpProviderStyle? HelpStyle =>
        IsLightBackground ? BuildLightHelpStyle() : null;

    private static bool Detect()
    {
        // 1. Explicit override: FUSERAFT_THEME=light|dark
        var forced = Environment.GetEnvironmentVariable("FUSERAFT_THEME");
        if (forced is not null)
            return forced.Equals("light", StringComparison.OrdinalIgnoreCase);

        // 2. TERM_BACKGROUND=light|dark — set by some shells and tools (bat, delta, fish)
        var termBg = Environment.GetEnvironmentVariable("TERM_BACKGROUND");
        if (termBg is not null)
            return termBg.Equals("light", StringComparison.OrdinalIgnoreCase);

        // 3. COLORFGBG=fg;bg — set by xterm, konsole, rxvt, etc.
        //    Last component is the background ANSI color index: 7 or 15 = light.
        var colorfgbg = Environment.GetEnvironmentVariable("COLORFGBG");
        if (colorfgbg is not null)
        {
            var parts = colorfgbg.Split(';');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var bg))
                return bg == 7 || bg == 15;
        }

        // 4. OSC 11 query — works in GNOME Terminal, Tilix, kitty, WezTerm, iTerm2, etc.
        //    Opens /dev/tty directly so it works even when stdout is piped.
        var osc = TryOsc11Query();
        if (osc.HasValue) return osc.Value;

        return false; // assume dark background
    }

    // -------------------------------------------------------------------------
    // OSC 11 background-colour query
    // -------------------------------------------------------------------------
    // Protocol: write ESC ] 11 ; ? BEL to the terminal.  It responds with
    //   ESC ] 11 ; rgb:RRRR/GGGG/BBBB BEL   (16-bit per channel)
    // We open /dev/tty directly and temporarily enable raw mode so the
    // response bytes are delivered immediately (not buffered until Enter).

    private static bool? TryOsc11Query()
    {
        if (!OperatingSystem.IsLinux()) return null;

        var ttyFd = LibcOpen("/dev/tty", 2 /* O_RDWR */, 0);
        if (ttyFd < 0) return null;

        try
        {
            // Save current terminal settings.
            var saved = new byte[128];
            if (Tcgetattr(ttyFd, saved) != 0) return null;

            var raw = (byte[])saved.Clone();

            // c_lflag is at byte offset 12 on Linux x86-64 (after three 4-byte flag fields).
            // Clear ICANON (0x0002) so responses aren't line-buffered,
            // and ECHO (0x0008) so the query bytes don't echo back.
            var lflag = BitConverter.ToUInt32(raw, 12);
            BitConverter.TryWriteBytes(new Span<byte>(raw, 12, 4), lflag & ~(0x0002u | 0x0008u));

            // c_cc starts at byte offset 17.  VTIME=index 5 (0.1s per-char timeout),
            // VMIN=index 6 (return as soon as ≥0 chars have arrived within VTIME).
            raw[17 + 5] = 1; // VTIME = 0.1 s
            raw[17 + 6] = 0; // VMIN  = 0

            if (Tcsetattr(ttyFd, 0 /* TCSANOW */, raw) != 0) return null;

            try
            {
                var q = "\x1b]11;?\x07"u8.ToArray();
                if (LibcWrite(ttyFd, q, q.Length) < 0) return null;

                var sb  = new StringBuilder(40);
                var buf = new byte[1];

                while (true)
                {
                    var n = LibcRead(ttyFd, buf, 1);
                    if (n <= 0) break; // timeout (VTIME expired with no data)

                    var ch = (char)buf[0];
                    sb.Append(ch);

                    if (ch == '\x07') break; // BEL terminator
                    if (sb.Length >= 2 && sb[^2] == '\x1b' && sb[^1] == '\\') break; // ST
                    if (sb.Length > 64) break; // safety guard
                }

                var m = OscRgbPattern.Match(sb.ToString());
                if (!m.Success) return null;

                // Responses use 16-bit (4 hex digit) components; take the high byte.
                var r = Convert.ToInt32(m.Groups[1].Value[..2], 16);
                var g = Convert.ToInt32(m.Groups[2].Value[..2], 16);
                var b = Convert.ToInt32(m.Groups[3].Value[..2], 16);
                return 0.299 * r + 0.587 * g + 0.114 * b > 127;
            }
            finally { Tcsetattr(ttyFd, 0, saved); }
        }
        finally { LibcClose(ttyFd); }
    }

    private static readonly Regex OscRgbPattern = new(
        @"rgb:([0-9a-fA-F]{2,4})/([0-9a-fA-F]{2,4})/([0-9a-fA-F]{2,4})",
        RegexOptions.Compiled);

    [DllImport("libc", EntryPoint = "open",     SetLastError = true)]
    private static extern int    LibcOpen([MarshalAs(UnmanagedType.LPStr)] string path, int flags, int mode);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int    LibcClose(int fd);
    [DllImport("libc", EntryPoint = "tcgetattr")]
    private static extern int    Tcgetattr(int fd, [Out] byte[] t);
    [DllImport("libc", EntryPoint = "tcsetattr")]
    private static extern int    Tcsetattr(int fd, int act, [In] byte[] t);
    [DllImport("libc", EntryPoint = "read")]
    private static extern int    LibcRead(int fd, [Out] byte[] buf, int count);
    [DllImport("libc", EntryPoint = "write")]
    private static extern int    LibcWrite(int fd, [In] byte[] buf, int count);

    // -------------------------------------------------------------------------
    // Light-mode help style
    // -------------------------------------------------------------------------
    // All colours are explicit dark values — never new Style() (null foreground)
    // which would fall back to the terminal's default and may be white.

    private static HelpProviderStyle BuildLightHelpStyle() => new()
    {
        Description = new DescriptionStyle
        {
            Header = new Style(Color.Olive),
        },
        Usage = new UsageStyle
        {
            Header          = new Style(Color.Olive),
            CurrentCommand  = new Style(null, null, Decoration.Underline),
            Command         = new Style(Color.Navy),
            Options         = new Style(Color.Grey),
            RequiredArgument = new Style(Color.Teal),
            OptionalArgument = new Style(Color.Grey),
        },
        Examples = new ExampleStyle
        {
            Header    = new Style(Color.Olive),
            Arguments = new Style(Color.Grey),
        },
        Arguments = new ArgumentStyle
        {
            Header           = new Style(Color.Olive),
            RequiredArgument = new Style(Color.Navy),
            OptionalArgument = new Style(Color.Grey),
        },
        Options = new OptionStyle
        {
            Header              = new Style(Color.Olive),
            DefaultValueHeader  = new Style(Color.Green),
            DefaultValue        = new Style(null, null, Decoration.Bold),
            RequiredOptionValue = new Style(Color.Grey),
            OptionalOptionValue = new Style(Color.Grey),
        },
        Commands = new CommandStyle
        {
            Header           = new Style(Color.Olive),
            ChildCommand     = new Style(Color.Navy),
            RequiredArgument = new Style(Color.Teal),
        },
    };
}
