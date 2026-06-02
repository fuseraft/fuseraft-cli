using fuseraft.Core.Models;

namespace fuseraft.Infrastructure;

/// <summary>
/// Coordinates creation, update, and progress queries for <see cref="Objective"/> records.
/// Delegates persistence to <see cref="ObjectiveStore"/>.
/// </summary>
public sealed class ObjectiveManager(ObjectiveStore store)
{
    public async Task<Objective> CreateAsync(
        string title,
        string description,
        IEnumerable<string>? remainingTasks = null,
        CancellationToken ct = default)
    {
        var id  = store.NextId();
        var obj = new Objective
        {
            Id             = id,
            Title          = title.Trim(),
            Description    = description.Trim(),
            Status         = "Active",
            RemainingTasks = remainingTasks?.Select(t => t.Trim()).ToList() ?? [],
            CreatedAt      = DateTimeOffset.UtcNow,
            UpdatedAt      = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(obj, ct);
        return obj;
    }

    public Task<Objective?> GetAsync(string id, CancellationToken ct = default)
        => store.GetAsync(id, ct);

    public Task<List<Objective>> ListAllAsync(CancellationToken ct = default)
        => store.LoadAllAsync(ct);

    public Task<List<Objective>> ListActiveAsync(CancellationToken ct = default)
        => store.LoadActiveAsync(ct);

    public async Task<Objective?> UpdateStatusAsync(
        string id, string status, CancellationToken ct = default)
    {
        var obj = await store.GetAsync(id, ct);
        if (obj is null) return null;

        obj = obj with { Status = status, UpdatedAt = DateTimeOffset.UtcNow };
        await store.SaveAsync(obj, ct);
        return obj;
    }

    public async Task<Objective?> UpdateAsync(
        string id,
        string? title = null,
        string? description = null,
        string? status = null,
        CancellationToken ct = default)
    {
        var obj = await store.GetAsync(id, ct);
        if (obj is null) return null;

        obj = obj with
        {
            Title       = title       ?? obj.Title,
            Description = description ?? obj.Description,
            Status      = status      ?? obj.Status,
            UpdatedAt   = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(obj, ct);
        return obj;
    }

    /// <summary>
    /// Moves <paramref name="task"/> to <c>CompletedTasks</c> (when <paramref name="completed"/> is true)
    /// or adds it to <c>RemainingTasks</c> (when false). Removes it from the other list if present.
    /// Also records <paramref name="sessionId"/> in <c>Sessions</c> when provided.
    /// </summary>
    public async Task<Objective?> LinkTaskAsync(
        string id,
        string task,
        bool completed,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var obj = await store.GetAsync(id, ct);
        if (obj is null) return null;

        var remaining  = obj.RemainingTasks.Where(t => t != task).ToList();
        var done       = obj.CompletedTasks.Where(t => t != task).ToList();
        var sessions   = obj.Sessions.ToList();

        if (completed)
            done.Add(task);
        else if (!remaining.Contains(task))
            remaining.Add(task);

        if (sessionId is not null && !sessions.Contains(sessionId))
            sessions.Add(sessionId);

        obj = obj with
        {
            CompletedTasks = done,
            RemainingTasks = remaining,
            Sessions       = sessions,
            UpdatedAt      = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(obj, ct);
        return obj;
    }

    /// <summary>
    /// Builds a compact summary block of active objectives for injection into agent prompts.
    /// Returns null when no active objectives exist.
    /// </summary>
    public async Task<string?> BuildActiveSummaryAsync(CancellationToken ct = default)
    {
        var active = await store.LoadActiveAsync(ct);
        if (active.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Active Objectives");
        foreach (var o in active)
        {
            var pct = o.PercentComplete;
            sb.Append($"[{o.Id}] {o.Title}");
            if (o.CompletedTasks.Count + o.RemainingTasks.Count > 0)
                sb.Append($" — {pct:F0}% complete ({o.CompletedTasks.Count}/{o.CompletedTasks.Count + o.RemainingTasks.Count} tasks)");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(o.Description))
                sb.AppendLine($"  {o.Description.Trim()}");
            if (o.RemainingTasks.Count > 0)
            {
                sb.AppendLine("  Remaining:");
                foreach (var t in o.RemainingTasks.Take(5))
                    sb.AppendLine($"    - {t}");
                if (o.RemainingTasks.Count > 5)
                    sb.AppendLine($"    … and {o.RemainingTasks.Count - 5} more");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
