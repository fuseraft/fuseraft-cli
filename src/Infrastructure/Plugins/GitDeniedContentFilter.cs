using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Removes the <i>contents</i> of files that a FileSystem deny rule protects from Git patch output
/// (<c>git diff</c>, <c>git show</c>, <c>git log -p</c>). Without this a tracked <c>.env</c> or
/// credentials file that <c>read_file</c> refuses to open could be read through the Git tools, since a
/// diff is file content wearing a different hat.
///
/// <para>
/// The section for a denied file keeps its <c>diff --git</c> header — the agent can still see that
/// the file changed — and its body (index line, <c>---</c>/<c>+++</c>, hunks, binary payload) is replaced
/// by a one-line notice. A section ends at the first line that can't be part of it, not at the next
/// blank line or a hunk-line count, so the header of the <i>next commit</i> in a <c>git log -p</c> is
/// never swallowed along with the file above it.
/// </para>
///
/// <para>
/// Paths in a diff header are relative to the repository top-level, so they are resolved against it
/// before the deny rule is applied. The header is ambiguous when a path contains spaces and, with
/// <c>diff.mnemonicPrefix</c> or <c>--no-prefix</c>, when it has no <c>a/</c>/<c>b/</c>; every plausible
/// reading of it is tested, so the failure mode is hiding an innocent file, never showing a denied one.
/// </para>
/// </summary>
internal static class GitDeniedContentFilter
{
    private static readonly string[] ExtendedHeaderPrefixes =
    [
        "index ", "old mode ", "new mode ", "deleted file mode ", "new file mode ",
        "similarity index ", "dissimilarity index ", "rename from ", "rename to ", "copy from ", "copy to ",
        "Binary files ",
    ];

    private static readonly string[] SectionHeaderPrefixes = ["diff --git ", "diff --cc ", "diff --combined "];

    /// <summary>Cheap pre-check: true when <paramref name="text"/> has a <c>diff --…</c> line at all.</summary>
    internal static bool ContainsPatch(string text) =>
        text.StartsWith("diff --", StringComparison.Ordinal) || text.Contains("\ndiff --", StringComparison.Ordinal);

    /// <summary>
    /// Returns <paramref name="output"/> with the body of every denied file's diff section replaced by
    /// a notice. <paramref name="repoTopLevel"/> is the repository's top-level directory.
    /// </summary>
    internal static string HideDeniedSections(
        string output, string repoTopLevel, Matcher? matcher, string? sandboxRoot, out IReadOnlyList<string> hiddenPaths)
    {
        var hidden = new List<string>();
        hiddenPaths = hidden;
        if (matcher is null || !ContainsPatch(output)) return output;

        var lines = output.Split('\n');
        var result = new List<string>(lines.Length);

        for (var i = 0; i < lines.Length;)
        {
            if (!IsSectionHeader(lines[i]))
            {
                result.Add(lines[i++]);
                continue;
            }

            var end = SectionEnd(lines, i);
            var deniedPath = CandidatePaths(lines, i, end)
                .FirstOrDefault(p => IsDenied(p, repoTopLevel, matcher, sandboxRoot));

            if (deniedPath is null)
            {
                for (var j = i; j < end; j++) result.Add(lines[j]);
            }
            else
            {
                result.Add(lines[i]);
                result.Add($"[content hidden: '{deniedPath}' matches a FileSystem deny rule]");
                hidden.Add(deniedPath);
            }
            i = end;
        }

        return string.Join('\n', result);
    }

    private static bool IsSectionHeader(string line) =>
        SectionHeaderPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal));

    // First line index that is NOT part of the section that starts at `start`. Everything a patch
    // emits for one file is either an extended-header line, a hunk line (' ', '+', '-', '\', '@'), or
    // a binary payload; anything else (a blank line, `commit …`, a log message) is not ours.
    private static int SectionEnd(string[] lines, int start)
    {
        var j = start + 1;
        while (j < lines.Length)
        {
            var line = lines[j];

            if (line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                j = SkipBinaryPatch(lines, j + 1);
                continue;
            }

            var isHunkOrHeaderLine =
                line.Length > 0 && (line[0] is ' ' or '+' or '-' or '\\' or '@')
                || ExtendedHeaderPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal));
            if (!isHunkOrHeaderLine) break;
            j++;
        }
        return j;
    }

    // "GIT binary patch" is followed by a `literal N`/`delta N` block of base85 lines ended by a blank
    // line, and usually a second (reverse) block. The base85 lines start with arbitrary letters, so
    // they can only be recognised by position.
    private static int SkipBinaryPatch(string[] lines, int j)
    {
        while (j < lines.Length
               && (lines[j].StartsWith("literal ", StringComparison.Ordinal) || lines[j].StartsWith("delta ", StringComparison.Ordinal)))
        {
            j++;
            while (j < lines.Length && lines[j].Length > 0 && lines[j] != "\r") j++;   // base85 body
            if (j < lines.Length) j++;                                                  // the blank line
        }
        return j;
    }

    private static List<string> CandidatePaths(string[] lines, int start, int end)
    {
        var paths = new List<string>();
        AddHeaderPaths(lines[start], paths);

        for (var j = start + 1; j < end; j++)
        {
            var line = lines[j].TrimEnd('\r');
            foreach (var prefix in new[] { "rename from ", "rename to ", "copy from ", "copy to " })
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    paths.Add(Unquote(line[prefix.Length..]));

            if ((line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
                && !line.EndsWith("/dev/null", StringComparison.Ordinal))
                paths.Add(Unquote(line[4..].TrimEnd('\t')));
        }

        // Each candidate with a one-letter `a/`-style prefix removed, then as written. Stripped first
        // so the path reported in the notice is the real one (`.env`), not `a/.env`.
        return paths.SelectMany(p => new[] { StripPrefix(p), p })
                    .Where(p => p.Length > 0)
                    .Distinct()
                    .ToList();
    }

    private static string StripPrefix(string path) =>
        path.Length > 2 && char.IsLetter(path[0]) && path[1] == '/' ? path[2..] : path;

    private static void AddHeaderPaths(string header, List<string> paths)
    {
        var prefix = SectionHeaderPrefixes.First(p => header.StartsWith(p, StringComparison.Ordinal));
        var rest = header[prefix.Length..].TrimEnd('\r');

        if (prefix != "diff --git ")           // --cc / --combined name a single path
        {
            paths.Add(Unquote(rest));
            return;
        }

        // "a/P b/P" (or quoted). With spaces in P the split point is ambiguous, so try every one.
        if (rest.StartsWith('"') && TryParseQuoted(rest, 0, out var first, out var endOfFirst))
        {
            paths.Add(first);
            paths.Add(Unquote(rest[endOfFirst..].TrimStart()));
        }
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] != ' ') continue;
            paths.Add(Unquote(rest[..i]));
            paths.Add(Unquote(rest[(i + 1)..]));
        }
        paths.Add(Unquote(rest));
    }

    private static bool IsDenied(string path, string repoTopLevel, Matcher matcher, string? sandboxRoot)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(repoTopLevel, path.Replace('\\', '/')));
            return FileSystemSandbox.MatchesDenyRule(matcher, full, sandboxRoot);
        }
        catch (ArgumentException) { return false; }   // illegal path characters — not a real file to deny
    }

    // git's core.quotePath form: "a/d\303\251/.env" — C escapes with octal bytes.
    private static string Unquote(string token)
    {
        token = token.Trim();
        return token.Length >= 2 && token[0] == '"' && TryParseQuoted(token, 0, out var value, out _) ? value : token;
    }

    private static bool TryParseQuoted(string s, int start, out string value, out int end)
    {
        value = string.Empty;
        end = start;
        if (start >= s.Length || s[start] != '"') return false;

        var bytes = new List<byte>();
        for (var i = start + 1; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"')
            {
                value = Encoding.UTF8.GetString([.. bytes]);
                end = i + 1;
                return true;
            }

            if (c != '\\' || i + 1 >= s.Length)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            var next = s[++i];
            if (next is >= '0' and <= '7')
            {
                var octal = next - '0';
                for (var k = 0; k < 2 && i + 1 < s.Length && s[i + 1] is >= '0' and <= '7'; k++)
                    octal = octal * 8 + (s[++i] - '0');
                bytes.Add((byte)octal);
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes((next switch { 'n' => "\n", 't' => "\t", 'r' => "\r", _ => next.ToString() })));
        }
        return false;   // unterminated
    }
}
