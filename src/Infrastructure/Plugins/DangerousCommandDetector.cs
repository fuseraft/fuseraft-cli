using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace fuseraft.Infrastructure.Plugins;

// Detail: the offending command's name, for rules whose message is about a specific command.
internal readonly record struct DangerousCommand(string RuleId, string Reason, string? Detail = null);

/// <summary>
/// Recognises the handful of shell commands that are never a legitimate thing for an agent to
/// run unattended — wiping the filesystem or a home directory, writing a raw block device,
/// piping a download straight into an interpreter, and escalating privileges with
/// <c>sudo</c>/<c>doas</c>/<c>pkexec</c> — so <see cref="ShellPlugin"/> can hard-deny them.
///
/// <para>
/// This is deliberately a tokenizer, not a substring list. A <c>ShellPolicy.Deny</c> entry of
/// <c>"rm -rf"</c> misses <c>rm  -rf</c>, <c>rm -fr</c>, <c>rm -r -f</c>, <c>/bin/rm -rf</c>,
/// <c>'rm' -rf</c> and <c>bash -c "rm -rf /"</c>. Here the command is normalised, split into
/// pipelines and stages with quoting/escaping/heredocs honoured, wrapper commands (<c>env</c>,
/// <c>nice</c>, <c>command</c>, ...) are looked through, and <c>sh -c</c> / <c>eval</c>
/// arguments are re-examined a few levels deep. It mirrors the <c>fetch-to-exec</c>,
/// <c>raw-disk-op</c> and <c>catastrophic-delete</c> rails in OpenHands' defense-in-depth
/// analyzer.
/// </para>
///
/// <para>
/// Precision matters more than coverage because a hit is a hard deny: <c>rm -rf build/</c>,
/// <c>dd if=a of=b</c>, <c>curl … | jq</c> and <c>curl … | python3 -c '…'</c> (stdin as data, not
/// as a script) must all pass. <b>Limitations:</b> POSIX-shell syntax only (no cmd.exe /
/// PowerShell rules), and static — variables, functions, <c>xargs</c> and multi-step
/// <c>curl -o x.sh … && sh x.sh</c> can still get past it. It is a guardrail beside the sandbox
/// and HITL approval, not a replacement for them.
/// </para>
/// </summary>
internal static class DangerousCommandDetector
{
    internal const string CatastrophicDelete = "catastrophic-delete";
    internal const string RawDiskOp          = "raw-disk-op";
    internal const string FetchToExec        = "fetch-to-exec";
    internal const string PrivilegeEscalation = "privilege-escalation";

    // sh -c "sh -c 'sh -c ...'" — enough to see through real wrappers without letting a
    // pathological input recurse without bound.
    private const int MaxNesting = 3;

    private static readonly HashSet<string> Shells =
        new(StringComparer.Ordinal) { "sh", "bash", "zsh", "dash", "ksh", "ash" };

    // Interpreters that only run stdin as a *script* when given no script/`-c`/`-m` argument.
    private static readonly HashSet<string> StdinInterpreters =
        new(StringComparer.Ordinal) { "python", "python2", "python3", "perl", "ruby", "node", "php" };

    // Run a command as another user. A regex for `sudo` at the start of a command misses
    // `/usr/bin/sudo`, `env sudo`, `command sudo`, `(sudo …)`, `$(sudo …)`, `then sudo` and quoted or
    // backslashed spellings; resolving the command word through the tokenizer does not.
    private static readonly HashSet<string> PrivilegeCommands =
        new(StringComparer.Ordinal) { "sudo", "sudoedit", "doas", "pkexec" };

    // Flags of xargs that consume the following argument, so it isn't mistaken for the command.
    private static readonly HashSet<string> XargsValueFlags =
        new(StringComparer.Ordinal) { "-n", "-I", "-L", "-P", "-s", "-d", "-E", "-a" };

    private static readonly HashSet<string> Fetchers =
        new(StringComparer.Ordinal) { "curl", "wget", "fetch" };

    // Shell grammar words that can precede the real command in a stage ("if rm -rf /; then").
    private static readonly HashSet<string> Reserved =
        new(StringComparer.Ordinal) { "if", "elif", "while", "until", "then", "do", "else", "time", "!", "{", "}" };

    // Commands that run another command, optionally after their own flags / a numeric argument.
    private static readonly HashSet<string> Wrappers =
        new(StringComparer.Ordinal) { "command", "builtin", "exec", "nohup", "env", "nice", "ionice", "timeout", "stdbuf", "setsid" };

    // Top-level directories whose recursive deletion is unrecoverable however it's spelled.
    private static readonly HashSet<string> SystemDirs = new(StringComparer.Ordinal)
    {
        "/", "/bin", "/boot", "/dev", "/etc", "/home", "/lib", "/lib32", "/lib64", "/opt", "/proc",
        "/root", "/sbin", "/srv", "/sys", "/usr", "/var", "/mnt", "/media",
        "/Users", "/System", "/Library", "/Applications", "/Volumes",
    };

    private static readonly Regex BlockDevice = new(
        @"^/dev/(?:sd|hd|vd|xvd|nvme|mmcblk|disk|rdisk|md|dm-|mapper/|mtd|loop|mem$|kmem$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Assignment = new(
        @"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Duration = new(
        @"^\d+(?:\.\d+)?[smhd]?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Fetch-to-exec with no pipe for the pipeline scan to see. Two exact shapes, each requiring the
    // download to *be* the script — `bash -c "echo $(curl …)"` merely prints it and must pass:
    //   process substitution:  bash <(curl …)   source <(wget …)   . <(curl …)
    //   command substitution:  bash -c "$(curl …)"   eval "$(curl …)"
    private const string FetcherWord = @"\s*(?:\S*/)?(?:curl|wget|fetch)\b";
    private const string LeadBoundary = @"(?:^|[\s;&|(`])";

    private static readonly Regex ProcessSubstitutedFetch = new(
        LeadBoundary + @"(?:(?:ba|z|da|k|a)?sh|source|\.)(?:\s+-\S+)*\s+<\(" + FetcherWord,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CommandSubstitutedFetch = new(
        LeadBoundary + @"(?:(?:(?:ba|z|da|k|a)?sh)\s+-\w*c\w*|eval)\s+[""']?(?:\$\(|`)" + FetcherWord,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static DangerousCommand? Detect(string? command) => Detect(command, 0);

    /// <summary>
    /// Folds compatibility forms (fullwidth letters etc.) and drops zero-width / other invisible
    /// format characters, so a deny rule can't be dodged by typography the shell never sees.
    /// Newlines and tabs are preserved — they are shell syntax.
    /// </summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var folded = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.Format) continue;
            if (cat == UnicodeCategory.Control && ch is not ('\n' or '\t' or '\r')) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static DangerousCommand? Detect(string? command, int depth)
    {
        if (depth > MaxNesting || string.IsNullOrWhiteSpace(command)) return null;

        var pipelines = Parse(Normalize(command), out var code);

        if (ProcessSubstitutedFetch.IsMatch(code) || CommandSubstitutedFetch.IsMatch(code))
            return new DangerousCommand(FetchToExec, "downloads remote content and executes it directly");

        foreach (var pipeline in pipelines)
        {
            var resolved = new List<(string Cmd, List<string> Args)?>(pipeline.Count);
            foreach (var stage in pipeline)
            {
                if (WritesBlockDevice(stage))
                    return new DangerousCommand(RawDiskOp, "writes directly to a raw block device");

                var r = Resolve(stage);
                resolved.Add(r);
                if (r is not { } cmd) continue;

                if (CheckStage(cmd.Cmd, cmd.Args, depth) is { } hit) return hit;
            }

            if (CheckFetchToExec(resolved) is { } fetch) return fetch;
        }

        return null;
    }

    private static DangerousCommand? CheckStage(string cmd, List<string> args, int depth)
    {
        if (PrivilegeCommands.Contains(cmd))
            return Privileged(cmd);
        if (CommandRunByXargsOrFind(cmd, args) is { } viaWrapper)
            return Privileged(viaWrapper);

        switch (cmd)
        {
            case "rm" when IsRecursiveRm(args, out var targets) && targets.Any(IsCatastrophicTarget):
                return Catastrophic();

            case "find" when IsDestructiveFind(args):
                return Catastrophic();

            case "dd" when args.Any(a => a.StartsWith("of=", StringComparison.Ordinal) && BlockDevice.IsMatch(a[3..])):
                return Raw();

            case "shred" or "wipefs" or "blkdiscard" when args.Any(a => BlockDevice.IsMatch(a)):
                return Raw();

            case "eval":
                return Detect(string.Join(' ', args), depth + 1);
        }

        if (cmd.StartsWith("mkfs", StringComparison.Ordinal) || cmd == "mke2fs")
            return Raw();

        // sh -c '<script>' / bash -lc "<script>": the script is a single word here, so look inside.
        if (Shells.Contains(cmd))
        {
            var c = args.FindIndex(IsCommandFlag);
            if (c >= 0 && c + 1 < args.Count)
                return Detect(args[c + 1], depth + 1);
        }

        return null;

        static DangerousCommand Privileged(string name) =>
            new(PrivilegeEscalation, "runs a command with elevated privileges", name);
        static DangerousCommand Catastrophic() =>
            new(CatastrophicDelete, "recursively deletes a system or home directory");
        static DangerousCommand Raw() =>
            new(RawDiskOp, "writes directly to a raw block device or formats a filesystem");
    }

    // The privilege command (if any) that `xargs <cmd>` or `find … -exec <cmd>` would run. Only the
    // command position counts: `xargs grep sudo` and `find / -name sudo` merely mention the word.
    private static string? CommandRunByXargsOrFind(string cmd, List<string> args)
    {
        if (cmd == "xargs")
        {
            for (var i = 0; i < args.Count; i++)
            {
                if (XargsValueFlags.Contains(args[i])) { i++; continue; }
                if (args[i].StartsWith('-')) continue;
                var name = Basename(args[i]);
                return PrivilegeCommands.Contains(name) ? name : null;
            }
            return null;
        }

        if (cmd == "find")
        {
            for (var i = 0; i + 1 < args.Count; i++)
            {
                if (args[i] is not ("-exec" or "-execdir" or "-ok" or "-okdir")) continue;
                var name = Basename(args[i + 1]);
                if (PrivilegeCommands.Contains(name)) return name;
            }
        }
        return null;
    }

    private static DangerousCommand? CheckFetchToExec(List<(string Cmd, List<string> Args)?> stages)
    {
        for (var i = 0; i < stages.Count - 1; i++)
        {
            if (stages[i] is not { } fetcher || !Fetchers.Contains(fetcher.Cmd)) continue;

            for (var j = i + 1; j < stages.Count; j++)
            {
                if (stages[j] is { } sink && ExecutesStdinAsScript(sink.Cmd, sink.Args))
                    return new DangerousCommand(FetchToExec, "downloads remote content and executes it directly");
            }
        }
        return null;
    }

    private static bool ExecutesStdinAsScript(string cmd, List<string> args)
    {
        if (Shells.Contains(cmd))
            return !args.Any(IsCommandFlag);   // `sh -c '…'` ignores stdin; plain / `-s` runs it

        if (StdinInterpreters.Contains(cmd))
            return args.Count == 0 || (args.Count == 1 && args[0] == "-");

        return false;
    }

    // A short-flag group that includes 'c' (-c, -lc, -ec) — the argument after it is the script.
    private static bool IsCommandFlag(string arg) =>
        arg.Length > 1 && arg[0] == '-' && arg[1] != '-' && arg.Contains('c');

    private static bool IsRecursiveRm(List<string> args, out List<string> targets)
    {
        var recursive = false;
        var endOfOptions = false;
        targets = [];

        foreach (var a in args)
        {
            if (!endOfOptions && a == "--") { endOfOptions = true; continue; }

            if (!endOfOptions && a.StartsWith("--", StringComparison.Ordinal))
            {
                if (a == "--recursive") recursive = true;
                continue;
            }

            if (!endOfOptions && a.Length > 1 && a[0] == '-')
            {
                if (a.AsSpan(1).IndexOfAny('r', 'R') >= 0) recursive = true;
                continue;
            }

            targets.Add(a);
        }
        return recursive;
    }

    // Predicates that narrow what `find` acts on. `-type` is deliberately absent: `find / -type f
    // -delete` still wipes every file.
    private static readonly HashSet<string> FindNarrowingPredicates = new(StringComparer.Ordinal)
    {
        "-name", "-iname", "-path", "-ipath", "-wholename", "-regex", "-iregex",
        "-newer", "-mtime", "-mmin", "-atime", "-ctime", "-size", "-perm", "-user", "-empty",
    };

    // An *unfiltered* delete sweep of a catastrophic root. `find ~ -name '*.pyc' -delete` is an
    // ordinary cleanup and must pass.
    private static bool IsDestructiveFind(List<string> args)
    {
        var roots = args
            .TakeWhile(a => !(a.StartsWith('-') || a is "(" or "!"))
            .ToList();
        if (roots.Count == 0 || !roots.Any(IsCatastrophicTarget)) return false;
        if (args.Any(FindNarrowingPredicates.Contains)) return false;

        return args.Contains("-delete")
            || ((args.Contains("-exec") || args.Contains("-execdir")) && args.Any(a => Basename(a) == "rm"));
    }

    // "/", "/*", "/etc", "/etc/", "/etc/*", "~", "~/", "~/*", "$HOME", "${HOME}", and the literal
    // path of the current user's home — but not "/etc/nginx" or "$HOME/project/build".
    private static bool IsCatastrophicTarget(string target)
    {
        var t = target.Trim();
        if (t.Length == 0) return false;

        var n = t;
        while (true)
        {
            if (n.EndsWith("/*", StringComparison.Ordinal) || n.EndsWith("/.", StringComparison.Ordinal))
                n = n[..^2];
            else if (n.Length > 1 && n.EndsWith('/'))
                n = n[..^1];
            else
                break;
        }

        if (n.Length == 0) return true;                       // "/*" collapsed to nothing → root
        if (n is "~" or "$HOME" or "${HOME}") return true;
        if (SystemDirs.Contains(n)) return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/');
        return home.Length > 1 && string.Equals(n, home, StringComparison.Ordinal);
    }

    // `> /dev/sda`, `>/dev/sda`, `>> /dev/nvme0n1` on any command in the stage.
    private static bool WritesBlockDevice(List<string> words)
    {
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var gt = w.IndexOf('>');
            if (gt < 0) continue;

            var target = w[(gt + 1)..].TrimStart('>', '|', '&');
            if (target.Length == 0 && i + 1 < words.Count) target = words[i + 1];
            if (BlockDevice.IsMatch(target)) return true;
        }
        return false;
    }

    // Looks through env-style assignments, grammar words and wrapper commands to the command
    // actually being run. Null when the stage has no command word (e.g. a bare `VAR=x`).
    private static (string Cmd, List<string> Args)? Resolve(List<string> words)
    {
        var i = 0;
        while (i < words.Count)
        {
            var w = words[i];
            if (Assignment.IsMatch(w)) { i++; continue; }

            var name = Basename(w);
            if (Reserved.Contains(name)) { i++; continue; }

            if (Wrappers.Contains(name))
            {
                i++;
                while (i < words.Count
                       && (words[i].StartsWith('-') || Assignment.IsMatch(words[i]) || Duration.IsMatch(words[i])))
                    i++;
                continue;
            }

            return (name, words.GetRange(i + 1, words.Count - i - 1));
        }
        return null;
    }

    private static string Basename(string word)
    {
        var slash = word.LastIndexOf('/');
        return (slash >= 0 ? word[(slash + 1)..] : word).ToLowerInvariant();
    }

    /// <summary>
    /// Splits shell text into pipelines (separated by <c>; && || &amp; newline</c> and by
    /// sub-shell / substitution delimiters) of stages (separated by a single <c>|</c>) of words,
    /// with quotes and escapes resolved. Comments and heredoc bodies are dropped: text that is
    /// only ever <i>written</i> to a file (a README that mentions <c>rm -rf /</c>) isn't a
    /// command.
    /// </summary>
    private static List<List<List<string>>> Parse(string s, out string code)
    {
        var skipped   = new List<(int Start, int End)>();
        var pipelines = new List<List<List<string>>>();
        var stages    = new List<List<string>>();
        var words     = new List<string>();
        var word      = new StringBuilder();
        var inWord    = false;
        var heredocs  = new Queue<(string Delimiter, bool StripTabs)>();

        void EndWord()
        {
            if (!inWord) return;
            var w = word.ToString();
            word.Clear();
            inWord = false;
            if (w != "$") words.Add(w);   // the '$' of a "$(" that was split off
        }
        void EndStage()
        {
            EndWord();
            if (words.Count == 0) return;
            stages.Add(words);
            words = [];
        }
        void EndPipeline()
        {
            EndStage();
            if (stages.Count == 0) return;
            pipelines.Add(stages);
            stages = [];
        }

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '\'':
                    inWord = true;
                    for (i++; i < s.Length && s[i] != '\''; i++) word.Append(s[i]);
                    break;

                case '"':
                    inWord = true;
                    for (i++; i < s.Length && s[i] != '"'; i++)
                    {
                        if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '"' or '\\' or '$' or '`') i++;
                        word.Append(s[i]);
                    }
                    break;

                case '\\':
                    if (i + 1 >= s.Length) break;
                    if (s[i + 1] == '\n') { i++; break; }      // line continuation
                    inWord = true;
                    word.Append(s[++i]);
                    break;

                case ' ' or '\t' or '\r':
                    EndWord();
                    break;

                case '\n':
                    EndPipeline();
                    while (heredocs.Count > 0)
                    {
                        var bodyStart = i + 1;
                        var bodyEnd   = SkipHeredocBody(s, bodyStart, heredocs.Dequeue());
                        skipped.Add((bodyStart, bodyEnd));
                        i = bodyEnd - 1;
                    }
                    break;

                case ';':
                    EndPipeline();
                    break;

                case '&':
                    // 2>&1, >&2, &>file are redirections, not control operators
                    if ((i > 0 && s[i - 1] is '>' or '<') || (i + 1 < s.Length && s[i + 1] == '>'))
                    {
                        inWord = true;
                        word.Append(c);
                        break;
                    }
                    if (i + 1 < s.Length && s[i + 1] == '&') i++;
                    EndPipeline();
                    break;

                case '|':
                    if (i + 1 < s.Length && s[i + 1] == '|') { i++; EndPipeline(); break; }
                    if (i + 1 < s.Length && s[i + 1] == '&') i++;
                    EndStage();
                    break;

                case '(' or ')' or '`':
                    EndPipeline();
                    break;

                case '#' when !inWord:
                    while (i + 1 < s.Length && s[i + 1] != '\n') i++;
                    break;

                case '<' when i + 1 < s.Length && s[i + 1] == '<' && !(i + 2 < s.Length && s[i + 2] == '<'):
                    EndWord();
                    i = ReadHeredocDelimiter(s, i + 2, out var delimiter, out var stripTabs) - 1;
                    if (delimiter.Length > 0) heredocs.Enqueue((delimiter, stripTabs));
                    break;

                default:
                    inWord = true;
                    word.Append(c);
                    break;
            }
        }

        EndPipeline();

        var sb = new StringBuilder(s.Length);
        var pos = 0;
        foreach (var (start, end) in skipped)
        {
            sb.Append(s, pos, start - pos);
            pos = end;
        }
        sb.Append(s, pos, s.Length - pos);
        code = sb.ToString();

        return pipelines;
    }

    // Reads `<<[-]['"]WORD['"]` starting just after the "<<"; returns the index after it.
    private static int ReadHeredocDelimiter(string s, int i, out string delimiter, out bool stripTabs)
    {
        stripTabs = false;
        if (i < s.Length && s[i] == '-') { stripTabs = true; i++; }
        while (i < s.Length && s[i] is ' ' or '\t') i++;

        var sb = new StringBuilder();
        for (; i < s.Length && s[i] is not (' ' or '\t' or '\n' or ';' or '&' or '|' or '<' or '>' or ')'); i++)
        {
            if (s[i] is '\'' or '"' or '\\') continue;
            sb.Append(s[i]);
        }
        delimiter = sb.ToString();
        return i;
    }

    // Returns the index just past the delimiter line (or end of input for an unterminated body).
    private static int SkipHeredocBody(string s, int i, (string Delimiter, bool StripTabs) doc)
    {
        while (i < s.Length)
        {
            var eol  = s.IndexOf('\n', i);
            var end  = eol < 0 ? s.Length : eol;
            var line = s[i..end].TrimEnd('\r');
            if (doc.StripTabs) line = line.TrimStart('\t');

            i = eol < 0 ? s.Length : eol + 1;
            if (line == doc.Delimiter) return i;
        }
        return i;
    }
}
