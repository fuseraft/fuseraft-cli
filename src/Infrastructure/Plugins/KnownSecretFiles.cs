using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Finds the secret <i>values</i> inside the files the FileSystem deny rules protect, so
/// <see cref="EnvSecretMasker"/> can hide them however a command happens to print them.
///
/// <para>
/// Deny rules stop the file tools from reading <c>.env</c>, and the shell policy stops a command
/// that <i>names</i> it. Neither can see <c>cat .e*</c>, <c>grep -r API_KEY .</c>, or a script that
/// opens the file itself — the command text doesn't spell the path out, and the output is just text.
/// Masking by value closes that gap: whatever prints <c>sk-live-…</c>, the model sees
/// <see cref="EnvSecretMasker.Placeholder"/> instead.
/// </para>
///
/// <para>
/// What counts as a secret in a denied file: the value of a secret-named <c>KEY=VALUE</c> /
/// <c>KEY: VALUE</c> line (the same name test as environment variables, plus <c>PASS</c>/<c>PWD</c>/
/// <c>DSN</c>), a password embedded in a URL (<c>postgres://user:PASSWORD@host</c>, every
/// <c>.git-credentials</c> line), the body of a PEM private key, a <c>.netrc</c> <c>password</c> and
/// the last field of a <c>.pgpass</c> line. Values shorter than
/// <see cref="EnvSecretMasker.MinValueLength"/> are skipped, as they are for the environment.
/// </para>
///
/// <para>
/// A source is a sandbox root plus its deny globs, registered once by the plugins that hand process
/// output to the model. The walk is bounded (depth, file count, well-known dependency directories
/// skipped, symlinked directories not followed) and cached for <see cref="CacheTtl"/>, so a masking
/// call costs a dictionary lookup. Like the environment masker this is exact-value: a model that
/// deliberately re-encodes a value gets past it.
/// </para>
/// </summary>
internal static class KnownSecretFiles
{
    internal static TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(10);

    private const int  MaxDepth       = 8;
    private const int  MaxFilesSeen   = 20_000;
    private const long MaxFileBytes   = 256 * 1024;

    private static readonly ConcurrentDictionary<string, Source> Sources = new();

    private sealed class Source(string? root, Matcher matcher, bool includeHome)
    {
        private readonly object _gate = new();
        private DateTime _scannedAt = DateTime.MinValue;
        private string[] _values = [];

        public string[] Values()
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _scannedAt >= CacheTtl)
                {
                    _values    = Scan(root, matcher, includeHome);
                    _scannedAt = DateTime.UtcNow;
                }
                return _values;
            }
        }

        public void Invalidate()
        {
            lock (_gate) _scannedAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Registers a sandbox root and its deny patterns. Idempotent. With
    /// <paramref name="includeHomeCredentials"/> the well-known credential files in the home directory
    /// (<c>~/.aws/credentials</c>, <c>~/.netrc</c>, <c>~/.pgpass</c>, <c>~/.git-credentials</c>,
    /// <c>~/.ssh/id_*</c>) are covered too — they are outside any sandbox root but are exactly what
    /// the credential-file guard protects.
    /// </summary>
    internal static void Track(string? sandboxRoot, IReadOnlyList<string>? denyPatterns, bool includeHomeCredentials)
    {
        var matcher = FileSystemSandbox.BuildDenyMatcher(denyPatterns);
        if (matcher is null && !includeHomeCredentials) return;
        matcher ??= new Matcher(StringComparison.OrdinalIgnoreCase);

        var key = string.Join('\n', sandboxRoot ?? "", includeHomeCredentials, string.Join('\n', denyPatterns ?? []));
        Sources.GetOrAdd(key, _ => new Source(sandboxRoot, matcher, includeHomeCredentials));
    }

    /// <summary>Every known secret value across all tracked sources, unordered and de-duplicated.</summary>
    internal static IReadOnlyCollection<string> Values()
    {
        if (Sources.IsEmpty) return [];
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Sources.Values)
            foreach (var value in source.Values())
                all.Add(value);
        return all;
    }

    /// <summary>Forces the next <see cref="Values"/> call to re-scan (tests, or after a known change).</summary>
    internal static void Invalidate()
    {
        foreach (var source in Sources.Values) source.Invalidate();
    }

    // Scanning

    private static string[] Scan(string? root, Matcher matcher, bool includeHome)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            if (root is not null && Directory.Exists(root))
                WalkRoot(root, matcher, values);
            if (includeHome)
                ScanHomeCredentials(values);
        }
        catch { /* a scan that fails must never break a tool call */ }

        return [.. values];
    }

    private static void WalkRoot(string root, Matcher matcher, HashSet<string> values)
    {
        var skip    = new HashSet<string>(DirectoryFilters.DefaultExcludedDirs, StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((root, 0));
        var seen = 0;

        while (pending.Count > 0 && seen < MaxFilesSeen)
        {
            var (dir, depth) = pending.Pop();

            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch { continue; }

            foreach (var entry in entries)
            {
                if (++seen > MaxFilesSeen) return;

                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch { continue; }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (depth < MaxDepth
                        && !attributes.HasFlag(FileAttributes.ReparsePoint)
                        && !skip.Contains(Path.GetFileName(entry)))
                        pending.Push((entry, depth + 1));
                    continue;
                }

                if (FileSystemSandbox.MatchesDenyRule(matcher, entry, root))
                    ReadSecrets(entry, values);
            }
        }
    }

    // Overridable so a test can point the home scan at a temp directory instead of the real one.
    internal static Func<string> UserHome { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static void ScanHomeCredentials(HashSet<string> values) => ScanHome(UserHome(), values);

    internal static void ScanHome(string home, HashSet<string> values)
    {
        if (string.IsNullOrEmpty(home)) return;

        foreach (var relative in new[] { ".aws/credentials", ".netrc", "_netrc", ".pgpass", ".git-credentials" })
            ReadSecrets(Path.Combine(home, relative), values);

        var ssh = Path.Combine(home, ".ssh");
        if (!Directory.Exists(ssh)) return;
        foreach (var key in Directory.EnumerateFiles(ssh, "id_*"))
            ReadSecrets(key, values);          // id_*.pub is read too and yields nothing: no secret-shaped content
    }

    private static void ReadSecrets(string path, HashSet<string> values)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > MaxFileBytes || BinaryFileSniffer.LooksBinary(path)) return;

            foreach (var secret in ExtractSecrets(info.Name, File.ReadAllText(path)))
                values.Add(secret);
        }
        catch { /* unreadable / vanished — nothing to learn from it */ }
    }

    // Extraction (pure)

    private static readonly Regex KeyValueLine = new(
        @"^\s*(?:export\s+)?[""']?(?<key>[A-Za-z_][A-Za-z0-9_.\-]*)[""']?\s*[=:]\s*(?<value>.+?)\s*[,;]?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Names EnvSecretMasker doesn't treat as secret for a live process, but which in a file that is
    // already deny-ruled are (DB_PASS, MONGO_PWD, SENTRY_DSN, an OAuth signing salt).
    private static readonly Regex ExtraSecretName = new(
        @"(?:^|[_.\-])(?:PASS|PWD|DSN|BEARER|SALT)(?:$|[_.\-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex UrlPassword = new(
        @"[A-Za-z][A-Za-z0-9+.\-]*://[^\s/:@]+:(?<pw>[^\s/@]+)@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NetrcPassword = new(
        @"\bpassword\s+(?<pw>\S+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PemBlock = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----(?<body>.*?)-----END [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>The secret values found in <paramref name="text"/>, the contents of a file called <paramref name="fileName"/>.</summary>
    internal static IEnumerable<string> ExtractSecrets(string fileName, string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? value)
        {
            if (value is { Length: >= EnvSecretMasker.MinValueLength })
                found.Add(value);
        }

        foreach (Match block in PemBlock.Matches(text))
        {
            var lines = block.Groups["body"].Value
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !l.Contains(':'))               // Proc-Type: / DEK-Info: headers are not key material
                .ToList();
            foreach (var line in lines) Add(line);
            Add(string.Concat(lines));                      // `tr -d '\n'` joins them
        }

        var isNetrc  = fileName is ".netrc" or "_netrc";
        var isPgpass = fileName == ".pgpass";

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            foreach (Match url in UrlPassword.Matches(line)) Add(url.Groups["pw"].Value);

            if (isNetrc)
                foreach (Match pw in NetrcPassword.Matches(line)) Add(pw.Groups["pw"].Value);

            if (isPgpass && line.Split(':') is { Length: 5 } fields)
                Add(fields[4].Trim());

            var kv = KeyValueLine.Match(line);
            if (kv.Success
                && (EnvSecretMasker.IsSensitiveName(kv.Groups["key"].Value) || ExtraSecretName.IsMatch(kv.Groups["key"].Value)))
                Add(Unquote(kv.Groups["value"].Value));
        }

        return found;
    }

    // `"abc"` / `'abc'` -> abc; an unquoted value loses a trailing ` # comment`.
    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
            return value[1..^1];

        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? value[..comment].TrimEnd() : value;
    }
}
