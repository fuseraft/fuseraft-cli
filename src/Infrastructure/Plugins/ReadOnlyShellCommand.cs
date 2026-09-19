using System.Text.RegularExpressions;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Decides whether a shell command is <i>obviously</i> read-only, so HITL mode can skip the y/N
/// prompt for it (<c>/hitl auto</c>). Prompting for every <c>ls</c> and <c>git status</c> trains people
/// to press <c>y</c> without reading — which is worse than a prompt that only appears for commands
/// that can change something.
///
/// <para>
/// The bar is deliberately high, because the failure direction is silently running something
/// that should have been approved. A command qualifies only if <b>every</b> simple command in it
/// (each pipeline stage, each <c>;</c>/<c>&amp;&amp;</c>/<c>||</c> segment, everything inside
/// <c>$( )</c> and <c>( )</c>) is on a fixed allowlist <i>and</i> carries no flag that turns it into
/// a writer or an executor — <c>find -exec</c>, <c>sort -o</c>, <c>git -c core.pager=…</c>,
/// <c>rg --pre</c>. Any output redirection to a file disqualifies it. Anything the tokenizer can't
/// enumerate with confidence (a substitution hidden in quotes) is "not read-only". When in doubt
/// the answer is <c>false</c> and the user is asked, as before.
/// </para>
///
/// <para>
/// "Read-only" is about side effects, not secrecy: <c>cat ~/.ssh/id_rsa</c> is read-only, and is
/// stopped by the credential-file guard and the sandbox, which run <i>before</i> approval.
/// Not a substitute for either.
/// </para>
/// </summary>
internal static class ReadOnlyShellCommand
{
    // Commands with no way to write a file or run another program, whatever their arguments.
    private static readonly HashSet<string> AlwaysReadOnly = new(StringComparer.Ordinal)
    {
        "ls", "dir", "pwd", "cd", "cat", "head", "tail", "wc", "nl", "tac", "rev",
        "stat", "du", "df", "which", "whereis", "type",
        "basename", "dirname", "realpath", "readlink",
        "uname", "whoami", "id", "groups", "uptime", "nproc", "arch", "lsb_release",
        "echo", "printf", "true", "false", ":", "test", "[", "[[", "seq", "printenv",
        "cut", "tr", "paste", "comm", "diff", "cmp", "column", "fold", "expand", "unexpand",
        "sha256sum", "sha1sum", "md5sum", "cksum", "b3sum", "od", "hexdump", "strings",
        "jq",
    };

    // grep-alikes read files and print; only ripgrep can shell out (--pre) and only via these flags.
    private static readonly HashSet<string> Searchers = new(StringComparer.Ordinal)
        { "grep", "egrep", "fgrep", "rg", "ag", "ack" };

    // git subcommands that only read. Flags that can write or execute are screened separately.
    private static readonly HashSet<string> GitReadOnly = new(StringComparer.Ordinal)
    {
        "status", "diff", "log", "show", "blame", "rev-parse", "rev-list", "ls-files", "ls-tree",
        "describe", "shortlog", "whatchanged", "cat-file", "diff-tree", "name-rev", "show-ref",
        "for-each-ref", "merge-base", "check-ignore", "count-objects", "grep", "reflog",
    };

    private static readonly HashSet<string> FindWriters = new(StringComparer.Ordinal)
        { "-exec", "-execdir", "-ok", "-okdir", "-delete", "-fprint", "-fprint0", "-fprintf", "-fls" };

    private static readonly HashSet<string> GitBranchWriters = new(StringComparer.Ordinal)
    {
        "-d", "-D", "--delete", "-m", "-M", "--move", "-c", "-C", "--copy", "-u", "--set-upstream-to",
        "--unset-upstream", "--edit-description", "-f", "--force",
    };

    // 2>&1, >&2, 1>&2 — duplicating a descriptor writes nothing to disk.
    private static readonly Regex FdDuplication = new(@"^\d*>+&(\d+|-)$", RegexOptions.Compiled);
    // A redirection operator on its own (target is the next word), or fused with /dev/null.
    private static readonly Regex BareRedirect  = new(@"^(\d*>+|&>+)$", RegexOptions.Compiled);
    private static readonly Regex NullRedirect  = new(@"^(\d*>+|&>+)/dev/null$", RegexOptions.Compiled);

    /// <summary>True only when every simple command in <paramref name="command"/> is provably read-only.</summary>
    internal static bool IsReadOnly(string? command)
    {
        var commands = DangerousCommandDetector.EnumerateCommands(command);
        return commands is { Count: > 0 } && commands.All(IsReadOnlyCommand);
    }

    private static bool IsReadOnlyCommand(ParsedCommand command)
    {
        // `LD_PRELOAD=./x.so ls`, `GIT_EXTERNAL_DIFF='…' git diff` and `PATH=./evil:$PATH; ls` all turn a
        // harmless-looking command into an executor, so an environment assignment disqualifies it.
        if (command.HasEnvironmentAssignment) return false;

        // The parser reduces the command word to its basename, so `./ls` or `/tmp/x/git` would
        // otherwise pass for the system tool. Only a bare name (resolved via PATH) or a standard
        // system directory is trusted; anything else is a program of unknown behaviour.
        if (!IsTrustedCommandWord(command.Text)) return false;

        var args = command.Args;
        if (WritesToAFile(args)) return false;

        var name = command.Name;
        if (AlwaysReadOnly.Contains(name)) return true;

        return name switch
        {
            _ when Searchers.Contains(name)
                => !args.Any(a => a.StartsWith("--pre", StringComparison.Ordinal)           // rg: run a program on each file
                                  || a.StartsWith("--hostname-bin", StringComparison.Ordinal)
                                  || a.StartsWith("--pager", StringComparison.Ordinal)),     // ag/ack: run a pager program
            "find"   => !args.Any(FindWriters.Contains),
            "sort"   => !args.Any(IsOutputFileFlag),
            "tree"   => !args.Any(IsOutputFileFlag),                       // tree -o FILE
            "file"   => !args.Contains("-C"),                              // file -C compiles a magic file to disk
            "date"   => !args.Any(a => a.StartsWith("--set", StringComparison.Ordinal) || IsShortFlagGroupWith(a, 's')),   // date -s sets the clock
            "hostname" => args.All(a => a.StartsWith('-')),                // `hostname NAME` renames the machine
            "uniq"   => args.Count(a => !a.StartsWith('-')) <= 1,   // a second operand is an output FILE
            "git"    => IsReadOnlyGit(args),
            _        => false,
        };
    }

    private static readonly string[] SystemBinDirs =
        ["/bin/", "/usr/bin/", "/usr/local/bin/", "/sbin/", "/usr/sbin/", "/opt/homebrew/bin/"];

    // `ls`, `git`, `/usr/bin/ls` — yes; `./ls`, `../x/git`, `/tmp/evil/ls`, `~/bin/ls` — no.
    private static bool IsTrustedCommandWord(string text)
    {
        var word = text.Split(' ', 2)[0];
        if (!word.Contains('/')) return true;

        return SystemBinDirs.Any(dir =>
            word.StartsWith(dir, StringComparison.Ordinal) && !word.AsSpan(dir.Length).Contains('/'));
    }

    // `sort -o file`, `sort -ofile`, `sort -nro file` (o inside a short-flag group), `--output=file`.
    private static bool IsOutputFileFlag(string arg) =>
        arg.StartsWith("--output", StringComparison.Ordinal) || IsShortFlagGroupWith(arg, 'o');

    private static bool IsShortFlagGroupWith(string arg, char flag) =>
        arg.Length > 1 && arg[0] == '-' && arg[1] != '-' && arg.Contains(flag);

    private static bool IsReadOnlyGit(IReadOnlyList<string> args)
    {
        // Global options before the subcommand. `-c key=value` can set core.pager / an alias to any
        // program, so it is never read-only; -C/--git-dir/--no-pager/… just pick a repo or a pager mode.
        var i = 0;
        for (; i < args.Count && args[i].StartsWith('-'); i++)
        {
            var flag = args[i];
            if (flag.StartsWith("-c", StringComparison.Ordinal)                 // -c key=value (lower-case; -C is a directory)
                || flag.StartsWith("--exec-path", StringComparison.Ordinal)     // where git looks for its subprograms
                || flag.StartsWith("--config-env", StringComparison.Ordinal))
                return false;
            if (flag is "-C" or "--git-dir" or "--work-tree" or "--namespace") i++;   // takes a value
        }
        if (i >= args.Count) return false;

        var sub  = args[i];
        var rest = args.Skip(i + 1).ToList();

        // These can write a file or run a program from the diff/log/show machinery.
        // (-O is `git grep --open-files-in-pager` and, for diff, a harmless orderfile; both just prompt.)
        if (rest.Any(a => a.StartsWith("--output", StringComparison.Ordinal)
                          || a is "--ext-diff" or "--open-files-in-pager"
                          || a.StartsWith("-O", StringComparison.Ordinal)))
            return false;

        return sub switch
        {
            "branch" => rest.All(a => a.StartsWith('-')) && !rest.Any(GitBranchWriters.Contains),
            "tag"    => rest.Count == 0
                        || (rest.Any(a => a is "-l" or "--list" or "-n")
                            && !rest.Any(a => a is "-d" or "--delete" or "-a" or "-s" or "-f" or "--force" or "-m" or "-F" or "-u" or "-v" or "--verify")),
            "remote" => rest.Count == 0 || rest is ["-v"] or ["--verbose"] or ["show", ..] or ["get-url", ..],
            "stash"  => rest is ["list", ..] or ["show", ..],
            "config" => rest.Any(a => a is "--get" or "--get-all" or "--get-regexp" or "--list" or "-l")
                        && !rest.Any(a => a is "--unset" or "--unset-all" or "--add" or "--replace-all" or "--edit" or "-e"),
            "reflog" => rest.Count == 0 || rest is ["show", ..] && !rest.Contains("--expire"),
            _        => GitReadOnly.Contains(sub),
        };
    }

    // Any redirection that isn't a descriptor duplication or a write to /dev/null could create or
    // truncate a file. Input redirection (`< file`) doesn't match and is fine.
    private static bool WritesToAFile(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var word = args[i];
            if (!word.Contains('>')) continue;
            if (FdDuplication.IsMatch(word) || NullRedirect.IsMatch(word)) continue;

            if (BareRedirect.IsMatch(word) && i + 1 < args.Count && args[i + 1] == "/dev/null")
            {
                i++;
                continue;
            }
            return true;
        }
        return false;
    }
}
