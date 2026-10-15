using System.Diagnostics;

namespace fuseraft.Cli.Tui;

/// <summary>
/// The workspace's files for completing @ mentions, kept in memory so a keystroke never waits for the disk. It's
/// refreshed in the background once it's older than a few seconds. Files git ignores, and files a deny rule covers
/// (.env, credentials), aren't offered.
/// </summary>
internal sealed class FileIndex(string root, Func<string, bool> denied)
{
    public const int MaxItems = 20;
    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);
    static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".fuseraft" };

    volatile List<string> _files = [];
    DateTime _loaded = DateTime.MinValue;
    int _loading;

    /// <summary>Starts loading, so the first @ already has something to offer.</summary>
    public FileIndex Warm()
    {
        Refresh();
        return this;
    }

    /// <summary>
    /// Paths that complete <paramref name="typed"/>: first the entries one level down from what's typed (directories end
    /// in "/"), then files anywhere whose name starts with its last part.
    /// </summary>
    public List<string> Complete(string typed)
    {
        if (DateTime.UtcNow - _loaded > Fresh)
            Refresh();
        return Complete(_files, typed);
    }

    public static List<string> Complete(IReadOnlyList<string> files, string typed)
    {
        var slash = typed.LastIndexOf('/');
        var (dir, name) = (typed[..(slash + 1)], typed[(slash + 1)..]);
        var level = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!file.StartsWith(dir, StringComparison.Ordinal))
                continue;
            var rest = file[dir.Length..];
            if (!rest.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var next = rest.IndexOf('/');
            level.Add(dir + (next < 0 ? rest : rest[..(next + 1)]));
        }
        List<string> items = [.. level.Take(MaxItems)];
        if (name.Length > 0)
            foreach (var file in files)
            {
                if (items.Count >= MaxItems)
                    break;
                if (Path.GetFileName(file).StartsWith(name, StringComparison.OrdinalIgnoreCase) && !items.Contains(file))
                    items.Add(file);
            }
        return items.Where(item => item != typed).ToList();
    }

    void Refresh()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                _files = [.. (GitFiles() ?? WalkFiles()).Where(f => !denied(f)).Order(StringComparer.Ordinal)];
                _loaded = DateTime.UtcNow;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                _loading = 0;
            }
        });
    }

    // Tracked and untracked files git doesn't ignore, relative to the root; null outside a repository.
    List<string>? GitFiles()
    {
        try
        {
            var psi = new ProcessStartInfo("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"])
            {
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode != 0 ? null
                : [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(f => File.Exists(Path.Combine(root, f))).Distinct()];
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    IEnumerable<string> WalkFiles()
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        Stack<string> dirs = new([root]);
        var count = 0;
        while (dirs.TryPop(out var dir) && count < 20_000)
        {
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                if (!SkippedDirs.Contains(Path.GetFileName(sub)))
                    dirs.Push(sub);
            foreach (var file in Directory.EnumerateFiles(dir, "*", options))
            {
                count++;
                yield return Path.GetRelativePath(root, file).Replace('\\', '/');
            }
        }
    }
}
