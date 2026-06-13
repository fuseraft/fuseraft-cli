namespace fuseraft.Core.Models.Session;

public abstract record ExecutionEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string SessionId { get; init; } = string.Empty;
    public int TurnIndex { get; init; }
    public string Agent { get; init; } = string.Empty;
}

public sealed record BuildResultEvent(
    bool Succeeded,
    int ExitCode,
    string Command,
    List<string> Errors,
    string? CommitHash = null) : ExecutionEvent;

public sealed record AttemptFailedEvent(
    string Description,
    string? ErrorSummary) : ExecutionEvent;

public sealed record AttemptSucceededEvent(
    string Description) : ExecutionEvent;

public sealed record TaskOpenedEvent(string Description) : ExecutionEvent;

public sealed record TaskCompletedEvent(string Description) : ExecutionEvent;
