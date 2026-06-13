namespace fuseraft.Core.Models.Config;

/// <summary>
/// Allow/deny policy for the Shell plugin. Evaluated before the command is executed.
/// Deny takes precedence: a command matching a deny pattern is blocked even if it also
/// matches an allow pattern.
/// </summary>
public record ShellPolicy
{
    /// <summary>
    /// When non-empty, only commands whose text contains at least one of these substrings
    /// (case-insensitive) are permitted. Acts as an allowlist: commands that do not match
    /// any pattern are rejected.
    /// Example: <c>["go test", "npm test", "dotnet test"]</c>.
    /// </summary>
    public List<string> Allow { get; init; } = [];

    /// <summary>
    /// Commands whose text contains any of these substrings (case-insensitive) are blocked
    /// regardless of the allow list.
    /// Example: <c>["rm -rf", "curl | bash", "wget | sh", "dd if="]</c>.
    /// </summary>
    public List<string> Deny { get; init; } = [];
}
