namespace fuseraft.Core.Models.Session;

/// <summary>
/// Projected operational ground truth for the current session.
/// Written to disk after every turn by <c>StateProjector</c>.
/// Never compacted — survives token pressure intact.
/// </summary>
public sealed record ExecutionState
{
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset LastUpdated { get; init; }
    public BuildState Build { get; init; } = new();
    public List<ValidationFailure> ActiveFailures { get; init; } = [];
    public List<AttemptRecord> FailedAttempts { get; init; } = [];
    public List<OpenTask> OpenTasks { get; init; } = [];
    public List<FileChangeRecord> SignificantChanges { get; init; } = [];
}

public sealed record BuildState
{
    public bool Succeeded { get; init; }
    public int ExitCode { get; init; }
    public string Command { get; init; } = string.Empty;
    public List<string> Errors { get; init; } = [];
    public string? LastGoodCommit { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record ValidationFailure
{
    public string Code { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
    public int Line { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record AttemptRecord
{
    public string Description { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? ErrorSummary { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record OpenTask
{
    public string Description { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public sealed record FileChangeRecord
{
    public string Path { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Written to execution-state.json alongside ExecutionState.
/// Orchestrator integration deferred to Phase 2 — model declared here for Phase 1.
/// </summary>
public sealed record AgentRoutingState
{
    public string CurrentOwner { get; init; } = string.Empty;
    public int ConsecutiveHandoffs { get; init; }
    public int LastSuccessfulTurn { get; init; } = -1;
}
