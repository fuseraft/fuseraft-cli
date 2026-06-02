namespace fuseraft.Core.Models;

/// <summary>
/// A long-horizon objective tracked across multiple sessions.
/// </summary>
public sealed record Objective
{
    public string Id          { get; init; } = string.Empty;
    public string Title       { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Active | Paused | Completed | Abandoned</summary>
    public string Status { get; init; } = "Active";

    public List<string> CompletedTasks  { get; init; } = [];
    public List<string> RemainingTasks  { get; init; } = [];
    public List<string> Sessions        { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Computed on demand — never stored. Returns 0 when no tasks are declared.
    /// </summary>
    public double PercentComplete
    {
        get
        {
            var total = CompletedTasks.Count + RemainingTasks.Count;
            return total == 0 ? 0.0 : (double)CompletedTasks.Count / total * 100.0;
        }
    }
}
