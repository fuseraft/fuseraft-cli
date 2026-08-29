namespace fuseraft.Core.Skills;

/// <summary>
/// Path-containment and symlink-escape checks for resolving a model-supplied relative path
/// against a trusted skill directory root.
///
/// <para>
/// <see cref="Path.GetFullPath(string)"/> only normalizes a path lexically (collapsing
/// <c>..</c> segments) — it does not resolve symbolic links. A lexical containment check alone
/// (<c>resolved.StartsWith(skillRoot)</c>) is therefore not sufficient: a symlink planted
/// anywhere inside a skill directory (e.g. <c>references</c> symlinked to <c>/etc</c>, or a
/// single file symlinked to <c>~/.ssh/id_rsa</c>) would pass that check while actually reading
/// or executing a file outside the skill. This mirrors the symlink-escape protection in
/// Microsoft's <c>AgentFileSkillsSource</c>, which fuseraft's orchestration skills provider is
/// built on.
/// </para>
/// </summary>
public static class SkillPathGuard
{
    /// <summary>
    /// Resolves <paramref name="relativePath"/> against <paramref name="skillRoot"/> and
    /// confirms the result stays inside the root with no symlinked path segment along the way.
    /// </summary>
    public static bool TryResolveSafePath(
        string skillRoot,
        string relativePath,
        out string fullPath,
        out string? reason)
    {
        var root        = Path.GetFullPath(skillRoot);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            reason = "path is outside the skill directory.";
            return false;
        }

        var relative = Path.GetRelativePath(root, fullPath);
        var current  = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue; // let the caller's own not-found handling report this

            FileAttributes attrs;
            try { attrs = File.GetAttributes(current); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                reason = $"'{segment}' is a symlink; skill paths may not traverse symlinks.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    /// <summary>True when <paramref name="path"/> exists and is a symlink/reparse point.</summary>
    public static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
