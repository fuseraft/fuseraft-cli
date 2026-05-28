namespace fuseraft.Core.Models;

/// <summary>
/// Granular glob-based access control for the FileSystem plugin.
/// Evaluated relative to <see cref="SecurityConfig.FileSystemSandboxPath"/> — requires a sandbox root.
/// All lists are optional; omitting them leaves the corresponding access type unrestricted within the sandbox.
/// </summary>
public record FileSystemPermissions
{
    /// <summary>
    /// When non-empty, restricts content-reading operations (<c>read_file</c>, <c>grep_file</c>,
    /// <c>get_file_summary</c>) to paths matching at least one of these glob patterns.
    /// Metadata-only operations (<c>list_files</c>, <c>list_directory</c>, <c>stat_file</c>,
    /// <c>path_exists</c>, <c>get_file_info</c>) are exempt — they return only names and
    /// timestamps, not file content. Use <c>Deny</c> to restrict those.
    /// </summary>
    public List<string> Read { get; init; } = [];

    /// <summary>
    /// When non-empty, restricts write operations (write_file, patch_file, delete_file,
    /// create_directory, delete_directory, copy_file, move_file, set_permissions) to paths
    /// matching at least one of these glob patterns. Evaluated alongside
    /// <see cref="SecurityConfig.ChangeEnvelope"/>; both must match when both are configured.
    /// </summary>
    public List<string> Write { get; init; } = [];

    /// <summary>
    /// Paths matching these globs are hard-denied for ALL operations (read and write).
    /// Checked before read/write allow lists and the change envelope — takes precedence over everything.
    /// Example: <c>["secrets/**", "infra/prod/**", ".env"]</c>.
    /// </summary>
    public List<string> Deny { get; init; } = [];
}
