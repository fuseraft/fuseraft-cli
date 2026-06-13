namespace fuseraft.Core.Models.Config;

/// <summary>
/// Security constraints applied to security-sensitive plugins at runtime.
/// All fields are optional; omitting them leaves the corresponding plugin unrestricted.
/// </summary>
public record SecurityConfig
{
    /// <summary>
    /// Restricts the <c>FileSystem</c> and <c>Shell</c> plugins to this directory tree.
    /// All resolved paths must be prefixed by this root; attempts to escape it are rejected.
    /// Set to the project working directory in production to prevent agents from accessing files outside the intended scope (credentials, SSH keys, etc.).
    /// Null means unrestricted (default).
    /// </summary>
    public string? FileSystemSandboxPath { get; init; }

    /// <summary>
    /// If non-empty, the <c>Http</c> plugin will only connect to the listed hostnames.
    /// Prevents agents from making SSRF-style requests to internal infrastructure or exfiltrating data to arbitrary hosts.
    /// Example: <c>["api.github.com", "registry.npmjs.org"]</c>.
    /// Empty list means unrestricted (default).
    /// </summary>
    public List<string> HttpAllowedHosts { get; init; } = [];

    /// <summary>
    /// When <c>true</c>, the private/loopback IP check in the <c>Http</c> plugin is skipped.
    /// Intended for local development and sandbox environments where agents must reach a
    /// locally-running mock API (e.g. <c>http://localhost:8000</c>).
    /// <b>Do not set this in production configs.</b>
    /// </summary>
    public bool AllowPrivateHosts { get; init; } = false;

    /// <summary>
    /// Maximum number of characters returned by a single <c>read_file</c> call.
    /// Larger files are truncated with a notice. Defaults to 20,000 (~5k tokens).
    /// Raise this when agents need to read large files in one call; lower it to reduce
    /// per-read token cost for agents with small context windows.
    /// </summary>
    public int ReadFileSizeLimit { get; init; } = 20_000;

    /// <summary>
    /// Restricts <em>write</em> operations (<c>write_file</c>, <c>patch_file</c>,
    /// <c>delete_file</c>) to files that match at least one of these glob patterns.
    /// Patterns are evaluated relative to <see cref="FileSystemSandboxPath"/> using
    /// standard glob syntax (<c>*</c>, <c>**</c>, <c>?</c>).
    /// Read operations are unaffected.
    ///
    /// <para>
    /// Typical use: set this in brownfield projects to the list of files the Planner
    /// scoped for the current task. Combined with <see cref="BrownfieldConfig.SeedEnvelopeFromBrief"/>,
    /// the Archaeologist's discovery brief populates this list automatically at startup.
    /// </para>
    ///
    /// Null or empty means all writes are allowed (within the sandbox root).
    /// Example: <c>["src/billing/**", "src/payments/processor.go"]</c>
    /// </summary>
    public List<string>? ChangeEnvelope { get; init; }

    /// <summary>
    /// Granular read/write/deny glob rules for the FileSystem plugin.
    /// Requires <see cref="FileSystemSandboxPath"/> — globs are evaluated relative to the sandbox root.
    /// Null means no additional glob-level access control (sandbox + change envelope still apply).
    /// </summary>
    public FileSystemPermissions? FileSystemPermissions { get; init; }

    /// <summary>
    /// Allow/deny substring policy applied to every Shell plugin command before execution.
    /// Works independently of <see cref="FileSystemSandboxPath"/> — shell policy is enforced
    /// even when no filesystem sandbox is configured.
    /// Null means the shell is unrestricted (subject to the existing sudo block).
    /// </summary>
    public ShellPolicy? ShellPolicy { get; init; }
}
