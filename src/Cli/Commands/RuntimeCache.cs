namespace fuseraft.Cli.Commands;

/// <summary>
/// Where the .NET host unpacks a single-file fuseraft's native libraries: one folder per build, which the host never clears, so
/// <c>fuseraft update</c> prunes it and <c>fuseraft uninstall</c> removes it.
/// </summary>
internal static class RuntimeCache
{
    /// <summary>This machine's cache root.</summary>
    public static string Root => RootFor(
        Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"),
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Path.GetTempPath());

    /// <summary><c>DOTNET_BUNDLE_EXTRACT_BASE_DIR</c>, else <c>%TEMP%\.net</c> on Windows and <c>~/.net</c> elsewhere, then the app's name.</summary>
    internal static string RootFor(string? extractBase, bool windows, string userProfile, string temp) =>
        Path.Combine(string.IsNullOrEmpty(extractBase) ? Path.Combine(windows ? temp : userProfile, ".net") : extractBase, "fuseraft");

    /// <summary>The build folder under <paramref name="root"/> this process loads its native libraries from; null when it isn't one.</summary>
    public static string? Current(string root) =>
        CurrentFrom(AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string, root);

    /// <summary>The entry of <paramref name="searchDirectories"/> (the host's native search path) that is a folder directly under <paramref name="root"/>.</summary>
    internal static string? CurrentFrom(string? searchDirectories, string root)
    {
        if (string.IsNullOrEmpty(searchDirectories)) return null;
        var normalizedRoot = Normalize(root);
        return searchDirectories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .FirstOrDefault(d => Path.GetDirectoryName(d) is { } parent
                                 && string.Equals(parent, normalizedRoot, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));

        static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Deletes every build folder under <paramref name="root"/> but <paramref name="keep"/>, skipping any still in use; the count and bytes freed.</summary>
    public static (int Count, long Bytes) PruneExcept(string root, string keep)
    {
        if (!Directory.Exists(root)) return (0, 0);
        var keepName = Path.GetFileName(keep.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        int count = 0;
        long bytes = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFileName(dir), keepName, StringComparison.OrdinalIgnoreCase)) continue;
            var size = Size(dir);
            try
            {
                Directory.Delete(dir, recursive: true);
                count++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a running older build still has it open */ }
        }
        return (count, bytes);
    }

    /// <summary>The total size of the files under <paramref name="dir"/>.</summary>
    public static long Size(string dir)
    {
        long bytes = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions
                     { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
                bytes += f.Length;
        }
        catch { /* the size is only a hint */ }
        return bytes;
    }

    /// <summary><paramref name="bytes"/> as KB, MB or GB.</summary>
    public static string FormatBytes(long bytes) =>
        bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):0.#} GB"
        : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB"
        : bytes > 0 ? $"{Math.Max(1, bytes / 1024)} KB" : "empty";
}
