namespace fuseraft.Core.Models;

/// <summary>
/// Granular glob-based access control for the FileSystem plugin.
/// Evaluated relative to <see cref="SecurityConfig.FileSystemSandboxPath"/> — requires a sandbox root.
/// All lists are optional; omitting them leaves the corresponding access type unrestricted within the sandbox.
/// </summary>
public record FileSystemPermissions
{
    /// <summary>
    /// When non-empty, restricts read operations (read_file, grep_file, list_files, stat_file,
    /// path_exists, list_directory, get_file_info, get_file_summary) to paths matching at least
    /// one of these glob patterns. Paths outside the read set are denied even within the sandbox.
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
