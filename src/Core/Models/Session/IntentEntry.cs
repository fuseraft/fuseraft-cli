namespace fuseraft.Core.Models.Session;

public enum IntentStatus
{
    Pending,
    Applied,
    Failed,
    Retryable
}

/// <summary>
/// Describes what a tool call intended to do — captured before execution so recovery
/// can replay or skip operations that were in-flight when the session was interrupted.
/// </summary>
public record IntentOperation
{
    public string FunctionName { get; init; } = string.Empty;

    /// <summary>Primary path argument (path / source / destination), if any.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Slim key→value summary of the call's arguments (values truncated to 200 chars).</summary>
    public Dictionary<string, string?> ArgsSummary { get; init; } = [];
}

/// <summary>
/// Immutable record of a single tool-call intent: created PENDING before the call executes,
/// updated to APPLIED or FAILED after it returns.
/// </summary>
public record IntentEntry
{
    public string IntentId { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string Agent { get; init; } = string.Empty;
    public int TurnIndex { get; init; }
    public string? SessionId { get; init; }
    public IntentOperation Operation { get; init; } = new();
    public IntentStatus Status { get; set; } = IntentStatus.Pending;
    public string? ErrorMessage { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>On-disk container for all intent entries in a session.</summary>
public record IntentStore
{
    public List<IntentEntry> Entries { get; init; } = [];
    public string? ActiveSessionId { get; init; }
}
