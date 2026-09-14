namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Resolves the default shell executable/flag once. Shared by <see cref="ShellPlugin"/>
/// (sandboxed, LLM-issued commands) and the REPL's <c>!&lt;command&gt;</c> shell escape
/// (unsandboxed, user-typed commands) so both agree on which shell runs a bare command.
/// </summary>
internal static class ShellEnvironment
{
    internal static readonly string Shell     = OperatingSystem.IsWindows() ? "cmd" : ResolveUnixShell();
    internal static readonly string ShellFlag = OperatingSystem.IsWindows() ? "/c"  : "-c";

    // Resolve bash from common locations so this works on NixOS, Alpine, and other
    // non-FHS distros where /bin/bash may not exist. Falls back to /bin/bash as a
    // last resort so the error message at least names the expected path.
    private static string ResolveUnixShell()
    {
        foreach (var candidate in new[] { "/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash" })
            if (File.Exists(candidate)) return candidate;
        return "/bin/bash";
    }
}
