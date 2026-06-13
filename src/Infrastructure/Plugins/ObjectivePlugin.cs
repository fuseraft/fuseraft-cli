using System.ComponentModel;
using System.Text;
using fuseraft.Infrastructure;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Agent-facing tools for long-horizon objective tracking.
///
/// Tool names (via <c>objective_</c> prefix):
///   objective_create    — record a new objective
///   objective_read      — fetch a single objective by ID
///   objective_update    — update title, description, or status
///   objective_list      — list all objectives (optionally filtered by status)
///   objective_link_task — add or complete a task linked to an objective
/// </summary>
public sealed class ObjectivePlugin
{
    private readonly ObjectiveManager _manager;

    public ObjectivePlugin(ObjectiveManager manager) => _manager = manager;

    [Description("Create a new long-horizon objective.")]
    public async Task<string> CreateAsync(
        [Description("Short descriptive title for the objective.")]
        string title,
        [Description("What this objective achieves and why it matters.")]
        string description = "",
        [Description("Comma-separated list of remaining tasks (optional).")]
        string? tasks = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return PluginResult.Error("title must not be empty.");

        var remaining = string.IsNullOrWhiteSpace(tasks)
            ? null
            : tasks.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0);

        var obj = await _manager.CreateAsync(title, description, remaining);
        return PluginResult.Ok($"Created {obj.Id}: {obj.Title}");
    }

    [Description("Read a long-horizon objective by ID.")]
    public async Task<string> ReadAsync(
        [Description("Objective ID, e.g. OBJ-0001.")]
        string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return PluginResult.Error("id must not be empty.");

        var obj = await _manager.GetAsync(id.Trim());
        return obj is null
            ? PluginResult.NotFound($"No objective with ID '{id}'.")
            : FormatFull(obj);
    }

    [Description("Update an objective's title, description, or status.")]
    public async Task<string> UpdateAsync(
        [Description("Objective ID to update.")]
        string id,
        [Description("New title (leave empty to keep current).")]
        string? title = null,
        [Description("New description (leave empty to keep current).")]
        string? description = null,
        [Description("New status: Active, Paused, Completed, or Abandoned.")]
        string? status = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return PluginResult.Error("id must not be empty.");

        var obj = await _manager.UpdateAsync(
            id.Trim(),
            string.IsNullOrWhiteSpace(title)       ? null : title.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            string.IsNullOrWhiteSpace(status)      ? null : status.Trim());

        return obj is null
            ? PluginResult.NotFound($"No objective with ID '{id}'.")
            : PluginResult.Ok($"Updated {obj.Id}: {obj.Title} (status: {obj.Status})");
    }

    [Description("List objectives, optionally filtered by status.")]
    public async Task<string> ListAsync(
        [Description("Filter by status: Active, Paused, Completed, Abandoned. Leave empty for all.")]
        string? status = null)
    {
        var all = await _manager.ListAllAsync();
        var filtered = string.IsNullOrWhiteSpace(status)
            ? all
            : all.Where(o => o.Status.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        if (filtered.Count == 0)
            return PluginResult.NotFound("No matching objectives found.");

        var sb = new StringBuilder();
        sb.AppendLine($"=== Objectives ({filtered.Count} result(s)) ===");
        foreach (var o in filtered)
        {
            sb.AppendLine();
            var pct = o.CompletedTasks.Count + o.RemainingTasks.Count > 0
                ? $" — {o.PercentComplete:F0}%"
                : string.Empty;
            sb.AppendLine($"[{o.Id}] {o.Title} ({o.Status}{pct})");
            if (!string.IsNullOrWhiteSpace(o.Description))
                sb.AppendLine($"  {o.Description.Trim()}");
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Mark a task as completed or add a pending task to an objective.")]
    public async Task<string> LinkTaskAsync(
        [Description("Objective ID, e.g. OBJ-0001.")]
        string id,
        [Description("Short task description.")]
        string task,
        [Description("True if the task is now completed; false to add it as a remaining task.")]
        bool completed = true,
        [Description("Current session ID to record (optional).")]
        string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(id))   return PluginResult.Error("id must not be empty.");
        if (string.IsNullOrWhiteSpace(task)) return PluginResult.Error("task must not be empty.");

        var obj = await _manager.LinkTaskAsync(id.Trim(), task.Trim(), completed, sessionId?.Trim());
        if (obj is null) return PluginResult.NotFound($"No objective with ID '{id}'.");

        var verb = completed ? "Completed" : "Added";
        return PluginResult.Ok($"{verb} task on {obj.Id} — progress: {obj.PercentComplete:F0}% ({obj.CompletedTasks.Count}/{obj.CompletedTasks.Count + obj.RemainingTasks.Count})");
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    private static string FormatFull(Objective o)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Id: {o.Id}");
        sb.AppendLine($"Title: {o.Title}");
        sb.AppendLine($"Status: {o.Status}");
        if (!string.IsNullOrWhiteSpace(o.Description))
            sb.AppendLine($"Description: {o.Description}");

        var total = o.CompletedTasks.Count + o.RemainingTasks.Count;
        if (total > 0)
            sb.AppendLine($"Progress: {o.PercentComplete:F0}% ({o.CompletedTasks.Count}/{total} tasks)");

        if (o.CompletedTasks.Count > 0)
        {
            sb.AppendLine("Completed Tasks:");
            foreach (var t in o.CompletedTasks) sb.AppendLine($"  ✓ {t}");
        }
        if (o.RemainingTasks.Count > 0)
        {
            sb.AppendLine("Remaining Tasks:");
            foreach (var t in o.RemainingTasks) sb.AppendLine($"  • {t}");
        }
        if (o.Sessions.Count > 0)
            sb.AppendLine($"Sessions: {string.Join(", ", o.Sessions)}");

        sb.AppendLine($"Created: {o.CreatedAt:yyyy-MM-dd}");
        sb.AppendLine($"Updated: {o.UpdatedAt:yyyy-MM-dd}");
        return sb.ToString().TrimEnd();
    }
}
