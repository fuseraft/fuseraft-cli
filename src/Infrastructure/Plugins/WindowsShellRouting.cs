using System.Text;
using System.Text.RegularExpressions;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>Which Windows shell a command runs in.</summary>
internal enum WindowsShell { Cmd, PowerShell }

/// <summary>
/// Decides whether a command runs in cmd.exe or PowerShell on Windows, and builds the PowerShell
/// invocation. Pure logic, so it is tested on every platform even though only Windows uses it.
///
/// <para>
/// PowerShell always gets its script through <c>-EncodedCommand</c> (base64 of UTF-16LE). Any
/// other route puts the script through at least one command-line parser — cmd.exe's, which knows
/// nothing of <c>\"</c> and splits on <c>|</c>, <c>&amp;</c> and <c>&gt;</c>, or the C runtime's,
/// which powershell.exe uses to split its own arguments — and a script full of quotes, such as one
/// carrying a connection string, rarely survives either.
/// </para>
/// </summary>
internal static class WindowsShellRouting
{
    /// <summary>
    /// Longest base64 payload passed on the command line. Windows caps a command line at 32,767
    /// chars; anything longer goes through a temp .ps1 file instead.
    /// </summary>
    internal const int MaxEncodedCommandChars = 30_000;

    /// <summary>
    /// Parses the tools' <c>shell</c> argument: null for "not given" (route automatically), or
    /// the shell named. Returns an error message for anything else.
    /// </summary>
    internal static string? ParseShellArg(string? shell, out WindowsShell? choice)
    {
        choice = null;
        switch (shell?.Trim().ToLowerInvariant())
        {
            case null or "" or "auto":
                return null;
            case "cmd" or "cmd.exe":
                choice = WindowsShell.Cmd;
                return null;
            case "powershell" or "powershell.exe" or "pwsh" or "pwsh.exe" or "ps":
                choice = WindowsShell.PowerShell;
                return null;
            default:
                return $"Unknown shell '{shell}'. Use 'cmd' or 'powershell', or leave it out.";
        }
    }

    /// <summary>
    /// The PowerShell script to run for <paramref name="command"/>, if it should run in PowerShell
    /// when no shell was named: an explicit <c>powershell</c>/<c>pwsh -Command …</c> invocation
    /// (its script, decoded the way powershell.exe would), or a command that is plainly PowerShell
    /// syntax (the command itself). Routing these up front, rather than running them in cmd.exe and
    /// retrying on failure, means nothing cmd.exe ran before failing runs a second time.
    /// </summary>
    internal static bool TryRouteToPowerShell(string command, out string script)
    {
        if (TryExtractPowerShellScript(command, out script))
            return true;
        if (LooksLikePowerShell(command))
        {
            script = command;
            return true;
        }
        script = string.Empty;
        return false;
    }

    private static readonly Regex PowerShellExe = new(
        @"^\s*""?(?:[^""]*?\\)?(?:powershell|pwsh)(?:\.exe)?""?(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// For a command that starts with <c>powershell</c>/<c>pwsh</c> and passes <c>-Command</c>
    /// (or <c>-c</c>), the script powershell.exe would run: the arguments after <c>-Command</c>,
    /// split with the C runtime's rules (so the <c>\"</c> a model wrote for cmd.exe's benefit
    /// becomes <c>"</c>) and joined with spaces. False for <c>-File</c>, <c>-EncodedCommand</c>,
    /// <c>-Command -</c>, or no <c>-Command</c> at all; those already work through cmd.exe.
    /// </summary>
    internal static bool TryExtractPowerShellScript(string command, out string script)
    {
        script = string.Empty;
        if (!PowerShellExe.IsMatch(command)) return false;

        var args = SplitCommandLine(command);
        var valueAllowed = false;   // a bare argument may follow a switch, as its value (-ExecutionPolicy Bypass)
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-') && !arg.StartsWith('/'))
            {
                if (!valueAllowed) return false;   // a positional script: not a -Command form
                valueAllowed = false;
                continue;
            }
            valueAllowed = true;
            if (!IsCommandSwitch(arg[1..])) continue;

            var rest = args.Skip(i + 1).ToList();
            if (rest.Count == 0 || rest is ["-"]) return false;
            script = string.Join(' ', rest).Trim();

            // A model writing for cmd.exe sometimes wraps the whole script in single quotes, which
            // PowerShell would print as a string rather than run. Unwrap only when they enclose it all.
            if (script.Length >= 2 && script[0] == '\'' && script[^1] == '\'' && script.IndexOf('\'', 1) == script.Length - 1)
                script = script[1..^1];

            return script.Length > 0;
        }
        return false;
    }

    // -Command and its unambiguous prefixes. "-co"/"-con" are left out: they also begin
    // -ConfigurationName.
    private static bool IsCommandSwitch(string name) =>
        name.Equals("c", StringComparison.OrdinalIgnoreCase) ||
        (name.Length >= 3 && "command".StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
         !name.Equals("con", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Splits a Windows command line the way the C runtime (and so powershell.exe) does: whitespace
    /// separates arguments outside double quotes; 2n backslashes before a quote give n backslashes
    /// and toggle quoting, 2n+1 give n backslashes and a literal quote; <c>""</c> inside quotes is
    /// a literal quote; other backslashes are literal.
    /// </summary>
    internal static List<string> SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasArg = false;
        var i = 0;

        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var start = i;
                while (i < commandLine.Length && commandLine[i] == '\\') i++;
                var count = i - start;
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1) { current.Append('"'); i++; }
                }
                else
                {
                    current.Append('\\', count);
                }
                hasArg = true;
                continue;
            }
            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i += 2;
                    continue;
                }
                inQuotes = !inQuotes;
                hasArg = true;
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasArg) { args.Add(current.ToString()); current.Clear(); hasArg = false; }
                i++;
                continue;
            }
            current.Append(c);
            hasArg = true;
            i++;
        }
        if (hasArg) args.Add(current.ToString());
        return args;
    }

    // A cmdlet name (approved verb, dash, noun) where a command starts: at the beginning, or after
    // a pipe, semicolon, or && / ||. Matched against the command with its quoted strings removed, so
    // text like git commit -m "fix Get-Item" doesn't count, and not when followed by a dot or dash:
    // start-dev.cmd or test-runner.exe is a program cmd.exe runs, not a cmdlet.
    private static readonly Regex CmdletAtCommandStart = new(
        @"(?:^|[|;&]\s*)(?:Add|Clear|Compare|Compress|ConvertFrom|ConvertTo|Copy|Expand|Export|ForEach|Format|Get|Group|Import|Invoke|Join|Measure|Move|New|Out|Remove|Rename|Resolve|Select|Set|Sort|Split|Start|Stop|Tee|Test|Where|Write)-[A-Za-z]+(?![\w.-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Syntax with no cmd.exe meaning, matched anywhere — PowerShell expands variables inside
    // double-quoted strings too: automatic and environment variables, static .NET member calls
    // ([IO.File]::ReadAllText), and the & call operator at the start.
    private static readonly Regex PowerShellOnlySyntax = new(
        @"\$env:\w|\$_\b|\$PSVersionTable\b|\$PSScriptRoot\b|\$LASTEXITCODE\b|\$(?:null|true|false)\b|\[[A-Za-z][\w.]*\]::\w|^\s*&\s*[""'$]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedString = new(@"""[^""]*""|'[^']*'", RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="command"/> is plainly PowerShell rather than cmd.exe syntax.</summary>
    internal static bool LooksLikePowerShell(string command) =>
        PowerShellOnlySyntax.IsMatch(command) ||
        CmdletAtCommandStart.IsMatch(QuotedString.Replace(command, "\"\""));

    /// <summary>
    /// The arguments that run <paramref name="script"/> in PowerShell with no command-line parsing
    /// in between, or null when the encoded script is too long for a command line.
    /// </summary>
    internal static string[]? EncodedCommandArgs(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Prelude + script));
        return encoded.Length > MaxEncodedCommandChars
            ? null
            : ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded];
    }

    /// <summary>The arguments that run the script file at <paramref name="ps1Path"/>.</summary>
    internal static string[] FileArgs(string ps1Path) =>
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ps1Path];

    /// <summary>
    /// The text to write to a .ps1 file for <paramref name="script"/>, and the encoding to write it
    /// in: UTF-8 with a byte-order mark, since Windows PowerShell 5.1 reads a file without one as
    /// the ANSI code page and mangles anything non-ASCII.
    /// </summary>
    internal static (string Text, Encoding Encoding) ScriptFile(string script) =>
        (Prelude + script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    // Progress bars (Invoke-WebRequest, Expand-Archive, …) have no console to draw on here; with
    // output redirected, Windows PowerShell writes them to stderr as CLIXML instead.
    private const string Prelude = "$ProgressPreference = 'SilentlyContinue'\n";
}
