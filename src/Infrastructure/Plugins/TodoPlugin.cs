using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Self-directed todo list the model uses to plan and track its own multi-step work within a
/// single REPL session. In-memory only — scoped to the session, not persisted to disk.
///
/// <para>
/// Unlike <see cref="ScratchpadPlugin"/> (free-form key/value notes) this holds one ordered
/// checklist that is always replaced wholesale on write, mirroring how coding-assistant todo
/// tools are conventionally used: the model writes the full plan up front, then rewrites the
/// full list after each step to flip statuses, rather than patching individual entries.
/// </para>
/// </summary>
public sealed class TodoPlugin
{
    private readonly Lock _lock = new();
    private List<TodoItem> _items = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> ValidStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "pending", "in_progress", "completed" };

    [Description(
        "Replace the current todo list with the given items. Use this to plan and track " +
        "multi-step or open-ended work: write the full plan before starting, then call this " +
        "again after each step completes or starts to update status. Always pass the complete " +
        "list, not just the changed item — this call replaces the whole list.")]
    public string Write(
        [Description(
            "JSON array of items, e.g. " +
            "[{\"content\":\"Read entry point\",\"status\":\"completed\"}," +
            "{\"content\":\"Map request flow\",\"status\":\"in_progress\"}]. " +
            "status is one of pending, in_progress, completed. Replaces the entire list.")]
        string itemsJson)
    {
        List<TodoItem>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<TodoItem>>(itemsJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            return $"[ERROR] Could not parse itemsJson as a JSON array: {ex.Message}";
        }
        if (parsed is null)
            return "[ERROR] itemsJson must be a JSON array of todo items.";

        foreach (var item in parsed)
        {
            if (string.IsNullOrWhiteSpace(item.Content))
                return "[ERROR] Every item needs non-empty 'content'.";
            if (!ValidStatuses.Contains(item.Status))
                return $"[ERROR] Invalid status '{item.Status}' on '{item.Content}' — use pending, in_progress, or completed.";
        }

        lock (_lock) _items = parsed;
        return Render(parsed);
    }

    [Description("Read the current todo list.")]
    public string Read()
    {
        List<TodoItem> snapshot;
        lock (_lock) snapshot = _items;
        return snapshot.Count == 0 ? "[EMPTY] No todo items." : Render(snapshot);
    }

    /// <summary>Snapshot for the REPL's own post-turn rendering — avoids re-parsing the tool's
    /// string return value just to show the checklist under the response.</summary>
    internal IReadOnlyList<TodoItem> Snapshot()
    {
        lock (_lock) return [.. _items];
    }

    internal static string Render(IReadOnlyList<TodoItem> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            var box = item.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ? "[x]"
                     : item.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) ? "[~]"
                     : "[ ]";
            sb.AppendLine($"{box} {item.Content}");
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed record TodoItem
{
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = "pending";
}
