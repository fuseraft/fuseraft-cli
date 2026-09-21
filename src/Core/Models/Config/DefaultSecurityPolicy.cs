namespace fuseraft.Core.Models.Config;

/// <summary>
/// "Don't leak secrets into context" baseline applied to every fuseraft session — REPL and
/// orchestration alike — regardless of whether the project declares a <see cref="SecurityConfig"/>
/// at all. A project's own <see cref="ShellPolicy.Deny"/> / <see cref="FileSystemPermissions.Deny"/>
/// are merged on top of this baseline, never replaced by it.
///
/// Single source of truth for the default pattern lists so REPL (<c>ReplCommand.cs</c>) and
/// orchestration (<c>PluginRegistry.Configure</c>, <c>AgentToolResolver</c>'s subagent tool
/// set) can't drift apart. See docs/security.md.
/// </summary>
internal static class DefaultSecurityPolicy
{
    /// <summary>
    /// Substring deny patterns merged into <see cref="ShellPolicy.Deny"/> (a shell command is matched
    /// as text, so these are plain substrings, not globs).
    /// </summary>
    internal static readonly IReadOnlyList<string> SecretDenyPatterns = [".env", ".env.*"];

    /// <summary>
    /// Glob deny patterns for the FileSystem plugin, matched against the path relative to the sandbox
    /// root. The leading <c>**/</c> is what makes them match at any depth: a bare <c>.env</c> glob only
    /// matches <c>&lt;root&gt;/.env</c>, which left <c>backend/.env</c> and <c>apps/web/.env.local</c>
    /// readable. Always on.
    /// </summary>
    internal static readonly IReadOnlyList<string> SecretFileGlobs = ["**/.env", "**/.env.*"];

    /// <summary>
    /// Files that hold credentials as their whole purpose. Deliberately narrow: mixed config files
    /// that merely <i>may</i> contain a token (<c>.npmrc</c>, <c>.docker/config.json</c>, <c>*.pem</c>
    /// certificates) are left alone, since blocking them breaks ordinary work. <c>id_rsa.pub</c> and
    /// the other public keys don't match. Controlled by <see cref="SecurityConfig.DenyCredentialFiles"/>.
    /// </summary>
    internal static readonly IReadOnlyList<string> CredentialFileGlobs =
    [
        "**/id_rsa", "**/id_dsa", "**/id_ecdsa", "**/id_ed25519",
        "**/.aws/credentials",
        "**/.netrc", "**/_netrc",
        "**/.pgpass",
        "**/.git-credentials",
    ];

    /// <summary>
    /// Merges <see cref="SecretDenyPatterns"/> into <paramref name="configured"/>'s Deny list
    /// (or produces a fresh policy carrying just the defaults, when none is configured).
    /// </summary>
    internal static ShellPolicy MergeShellPolicy(ShellPolicy? configured) =>
        (configured ?? new ShellPolicy()) with { Deny = MergePatterns(SecretDenyPatterns, configured?.Deny) };

    /// <summary>
    /// Merges <see cref="SecretFileGlobs"/> — and, unless opted out, <see cref="CredentialFileGlobs"/> —
    /// into <paramref name="configured"/>'s Deny list; the shape
    /// <see cref="fuseraft.Infrastructure.Plugins.FileSystemPlugin"/>'s <c>denyPatterns</c> constructor
    /// parameter expects directly.
    /// </summary>
    internal static List<string> MergeFileSystemDeny(FileSystemPermissions? configured, bool denyCredentialFiles = true) =>
        MergePatterns(
            denyCredentialFiles ? [.. SecretFileGlobs, .. CredentialFileGlobs] : SecretFileGlobs,
            configured?.Deny);

    private static List<string> MergePatterns(IReadOnlyList<string> defaults, IReadOnlyList<string>? configured) =>
        defaults.Concat(configured ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
