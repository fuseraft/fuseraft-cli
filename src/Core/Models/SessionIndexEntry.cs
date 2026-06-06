namespace fuseraft.Core.Models;

/// <summary>
/// Lightweight per-session metadata stored in <c>~/.fuseraft/sessions/index.json</c>.
/// Contains only the fields needed for listing and searching — no message history.
/// </summary>
public record SessionIndexEntry
{
    public required string SessionId     { get; init; }

    /// <summary>First non-empty line of the task, truncated to 120 chars.</summary>
    public required string Task          { get; init; }

    /// <summary>Working directory at session start. Null for sessions created before this field was introduced.</summary>
    public string? WorkingDirectory      { get; init; }

    public string? ConfigPath            { get; init; }
    public DateTime StartedAt            { get; init; }
    public DateTime LastUpdatedAt        { get; init; }
    public bool IsComplete               { get; init; }
    public int TurnCount                 { get; init; }
}
