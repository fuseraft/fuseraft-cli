using System.Text.Json.Serialization;

namespace fuseraft.Core.Models;

/// <summary>
/// On-disk change log. A single JSON file accumulates one <see cref="ChangeEntry"/> per
/// agent turn, recording what was actually done (files written, commands run, commits made)
/// rather than what an agent claimed to do in prose.
/// </summary>
public record ChangeLog
{
    /// <summary>All persisted per-turn change entries in chronological order.</summary>
    public List<ChangeEntry> Entries { get; init; } = [];

    /// <summary>
    /// The session ID that is currently active. Set by <c>ChangeTracker.SetSessionIdAsync</c>
    /// at the start of each session so <c>TestReportValid</c> check 8 can filter to
    /// only the commands recorded in the current session, preventing prior-session
    /// commands from satisfying the cross-reference check.
    /// </summary>
    public string? ActiveSessionId { get; init; }
}

/// <summary>
/// Per-turn record of tool calls that completed during one agent response.
/// </summary>
public record ChangeEntry
{
    /// <summary>Name of the agent that produced the recorded tool activity.</summary>
    public string Agent { get; init; } = string.Empty;
    /// <summary>Zero-based turn index for the agent response that produced this entry.</summary>
    public int TurnIndex { get; init; }
    /// <summary>UTC timestamp when the entry was recorded.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Session that produced this entry. Null for entries written before session-ID stamping was introduced.</summary>
    public string? SessionId { get; init; }

    /// <summary>Files successfully written this turn (paths from write_file calls).</summary>
    public List<string> FilesWritten { get; init; } = [];

    /// <summary>Files successfully deleted this turn.</summary>
    public List<string> FilesDeleted { get; init; } = [];

    /// <summary>Shell commands executed this turn, with their success status.</summary>
    public List<CommandRecord> CommandsRun { get; init; } = [];

    /// <summary>Git commit messages from successful git_commit calls.</summary>
    public List<string> GitCommits { get; init; } = [];
}

public record CommandRecord
{
    /// <summary>Exact shell command that was executed.</summary>
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    /// <summary>
    /// Combined stdout/stderr returned by the shell plugin, capped at 4 096 characters.
    /// Null for entries written before output capture was introduced.
    /// Used by <c>HandoffToReviewerValidator</c> to verify test-report results are grounded
    /// in real command output rather than cross-referencing fragile command strings.
    /// </summary>
    public string? Output { get; init; }
}
