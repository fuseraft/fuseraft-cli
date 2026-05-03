namespace fuseraft.Core.Models;

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
}
