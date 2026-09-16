namespace fuseraft.Core.Models.Config;

/// <summary>
/// "Don't leak secrets into context" baseline applied to every fuseraft session — REPL and
/// orchestration alike — regardless of whether the project declares a <see cref="SecurityConfig"/>
/// at all. A project's own <see cref="ShellPolicy.Deny"/> / <see cref="FileSystemPermissions.Deny"/>
/// are merged on top of this baseline, never replaced by it.
///
/// Single source of truth for the default pattern list so REPL (<c>ReplCommand.cs</c>) and
/// orchestration (<c>PluginRegistry.Configure</c>, <c>AgentToolResolver</c>'s sub-agent tool
/// set) can't drift apart. See docs/security.md.
/// </summary>
internal static class DefaultSecurityPolicy
{
    internal static readonly IReadOnlyList<string> SecretDenyPatterns = [".env", ".env.*"];

    /// <summary>
    /// Merges <see cref="SecretDenyPatterns"/> into <paramref name="configured"/>'s Deny list
    /// (or produces a fresh policy carrying just the defaults, when none is configured).
    /// </summary>
    internal static ShellPolicy MergeShellPolicy(ShellPolicy? configured) =>
        (configured ?? new ShellPolicy()) with { Deny = MergePatterns(configured?.Deny) };

    /// <summary>
    /// Merges <see cref="SecretDenyPatterns"/> into <paramref name="configured"/>'s Deny list —
    /// the shape <see cref="fuseraft.Infrastructure.Plugins.FileSystemPlugin"/>'s
    /// <c>denyPatterns</c> constructor parameter expects directly.
    /// </summary>
    internal static List<string> MergeFileSystemDeny(FileSystemPermissions? configured) =>
        MergePatterns(configured?.Deny);

    private static List<string> MergePatterns(IReadOnlyList<string>? configured) =>
        SecretDenyPatterns.Concat(configured ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
