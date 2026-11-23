using System.Text;
using System.Text.Json;

namespace fuseraft.Cli.Commands.Uninstall;

/// <summary>The decisions <see cref="UninstallCommand"/> makes, kept free of side effects so each can be tested.</summary>
internal static class UninstallPlan
{
    internal const string UpdaterName = "fuseraft-update.exe";

    // The entries a fuseraft home is made of; a directory holding none of them isn't one.
    private static readonly string[] HomeMarkers =
        ["config", "repl-sessions", "sessions", "logs", "state", "skills", "memory", "schedule", "knowledge", "crashdump", "run"];

    /// <summary>Whether this is root acting for another user through sudo, whose own data and keys a purge would miss.</summary>
    internal static bool UnderSudo(string userName, string? sudoUser) =>
        userName == "root" && !string.IsNullOrEmpty(sudoUser) && sudoUser != "root";

    /// <summary>The installed CLI's path, or null when this process isn't one (e.g. a development build run through <c>dotnet</c>).</summary>
    internal static string? InstalledBinary(string? processPath) =>
        !string.IsNullOrEmpty(processPath)
        && Path.GetFileNameWithoutExtension(processPath).Equals("fuseraft", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : null;

    /// <summary>The files an install or update leaves beside <paramref name="binary"/>, the binary itself first.</summary>
    internal static IReadOnlyList<string> InstallFiles(string binary, bool windows)
    {
        var dir   = Path.GetDirectoryName(binary)!;
        var files = new List<string> { binary, binary + ".new" };
        if (windows)
        {
            files.Add(binary + ".pending");
            files.Add(binary + ".backup");
            files.Add(Path.Combine(dir, UpdaterName));
            files.Add(Path.Combine(dir, UpdaterName + ".new"));
        }
        return files;
    }

    /// <summary>
    /// The directories install.ps1 created for <paramref name="binary"/> (<c>…\fuseraft\bin</c> and <c>…\fuseraft</c>), innermost
    /// first; empty when it was installed somewhere else, which may be a folder other tools share.
    /// </summary>
    internal static IReadOnlyList<string> InstallDirs(string binary)
    {
        var bin    = Path.GetDirectoryName(binary)!;
        var parent = Path.GetDirectoryName(bin);
        return Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase)
               && parent is not null
               && Path.GetFileName(parent).Equals("fuseraft", StringComparison.OrdinalIgnoreCase)
            ? [bin, parent]
            : [];
    }

    /// <summary>
    /// <paramref name="pathValue"/> (a <c>;</c>-separated Windows PATH) without the entries naming <paramref name="dir"/>.
    /// Entries are compared after <paramref name="expand"/>, so <c>%LOCALAPPDATA%\fuseraft\bin</c> matches; the others are kept as written.
    /// </summary>
    internal static (string Value, bool Removed) RemovePathEntry(string pathValue, string dir, Func<string, string> expand)
    {
        var target  = NormalizeDir(dir);
        var entries = pathValue.Split(';');
        var kept    = entries.Where(e => e.Trim().Length == 0 || NormalizeDir(expand(e)) != target).ToList();
        if (kept.Count == entries.Length) return (pathValue, false);
        return (string.Join(';', kept.Where(e => e.Trim().Length > 0)), true);
    }

    private static string NormalizeDir(string dir) =>
        dir.Trim().Trim('"').TrimEnd('\\', '/').ToUpperInvariant();

    /// <summary>Why <paramref name="root"/> must not be deleted as a fuseraft home, or null when it's safe to.</summary>
    internal static string? PurgeRefusal(string root, string userProfile)
    {
        var full    = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var profile = Path.GetFullPath(userProfile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cmp     = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (full.Length == 0 || Path.GetPathRoot(full + Path.DirectorySeparatorChar)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(full, cmp) == true)
            return $"{root} is the root of a drive.";
        if (string.Equals(full, profile, cmp) || (profile + Path.DirectorySeparatorChar).StartsWith(full + Path.DirectorySeparatorChar, cmp))
            return $"{root} is your home folder or contains it.";
        if (!Directory.Exists(full)) return null;

        var names = Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName).ToList();
        if (names.Count == 0 || Path.GetFileName(full).Equals(".fuseraft", cmp)) return null;
        return names.Any(n => HomeMarkers.Contains(n, StringComparer.OrdinalIgnoreCase))
            ? null
            : $"{root} doesn't look like a fuseraft home (no config, sessions or logs in it).";
    }

    /// <summary>
    /// The project folders fuseraft ran in that still have a <c>.fuseraft/</c> folder, read from the saved chats and sessions under
    /// <paramref name="globalRoot"/>. The home's own folder never counts, though a chat started in the home folder points at it.
    /// </summary>
    internal static IReadOnlyList<string> ProjectFolders(string globalRoot)
    {
        var projects = new SortedSet<string>(StringComparer.Ordinal);
        var home     = Path.GetFullPath(globalRoot).TrimEnd(Path.DirectorySeparatorChar);

        void Scan(string dir, string property)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var cwd = ReadTopLevelString(file, property);
                if (string.IsNullOrWhiteSpace(cwd)) continue;
                string local;
                try { local = Path.GetFullPath(Path.Combine(cwd, ".fuseraft")); }
                catch { continue; }
                if (local.TrimEnd(Path.DirectorySeparatorChar) != home && Directory.Exists(local))
                    projects.Add(Path.GetFullPath(cwd));
            }
        }

        Scan(Path.Combine(globalRoot, "repl-sessions"), "Cwd");
        Scan(Path.Combine(globalRoot, "sessions"), "WorkingDirectory");
        return [.. projects];
    }

    /// <summary>A top-level string property near the start of a JSON file, read from its first 256 KB so a long history isn't loaded.</summary>
    internal static string? ReadTopLevelString(string file, string property)
    {
        byte[] buffer;
        int read;
        try
        {
            using var fs = File.OpenRead(file);
            buffer = new byte[Math.Min(fs.Length, 256 * 1024)];
            read   = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch { return null; }

        var reader = new Utf8JsonReader(buffer.AsSpan(0, read), isFinalBlock: false, state: default);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    var match = reader.ValueTextEquals(property);
                    if (!reader.Read()) return null;
                    if (match) return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip()) return null;
                }
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>What a keychain account holds, in words.</summary>
    internal static string KeychainLabel(string account)
    {
        if (account == "default") return "API key (saved before keys were per endpoint)";
        if (account.StartsWith("provider:", StringComparison.Ordinal)) return $"API key for {account["provider:".Length..]}";
        if (account.StartsWith("mcp-oauth-", StringComparison.Ordinal))
        {
            var name = account["mcp-oauth-".Length..];
            var dash = name.LastIndexOf('-');
            return $"MCP sign-in for {(dash > 0 ? name[..dash] : name)}";
        }
        return account;
    }

    /// <summary>The active lines of a crontab that run fuseraft.</summary>
    internal static IReadOnlyList<string> CronLines(string crontab) =>
        crontab.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains("fuseraft", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// A PowerShell script that waits for process <paramref name="pid"/> to exit, then deletes <paramref name="files"/> and
    /// <paramref name="trees"/> (retrying while something still has them open) and each of <paramref name="dirs"/> that's left empty.
    /// A running exe can't delete itself on Windows, nor the native libraries it has loaded.
    /// </summary>
    internal static string WindowsCleanupScript(int pid, IEnumerable<string> files, IEnumerable<string> trees, IEnumerable<string> dirs)
    {
        static string List(IEnumerable<string> items) => "@(" + string.Join(", ", items.Select(i => "'" + i.Replace("'", "''") + "'")) + ")";

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine($"Wait-Process -Id {pid} -Timeout 120");
        sb.AppendLine($"$files = {List(files)} + {List(trees)}");
        sb.AppendLine("for ($i = 0; $i -lt 60; $i++) {");
        sb.AppendLine("  foreach ($f in $files) { Remove-Item -LiteralPath $f -Recurse -Force }");
        sb.AppendLine("  if (-not ($files | Where-Object { Test-Path -LiteralPath $_ })) { break }");
        sb.AppendLine("  Start-Sleep -Seconds 1");
        sb.AppendLine("}");
        sb.AppendLine($"foreach ($d in {List(dirs)}) {{");
        sb.AppendLine("  if ((Test-Path -LiteralPath $d) -and -not (Get-ChildItem -LiteralPath $d -Force)) { Remove-Item -LiteralPath $d -Force }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
