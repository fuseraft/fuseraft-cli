using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Core;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Persistent per-agent scratchpad that survives across sessions.
///
/// <para>
/// Each agent gets an isolated JSON file at <c>{BasePath}/{AgentName}.json</c>.
/// A <c>global</c> scope (<c>{BasePath}/global.json</c>) allows agents to share
/// facts across the orchestration. Agents switch scope by passing <c>scope: "global"</c>
/// to any function.
/// </para>
///
/// <para>
/// Typical usage pattern in agent instructions: at the start of a resumed session,
/// call <c>scratchpad_read_all</c> to restore context from prior sessions. Write new
/// decisions or facts with <c>scratchpad_write</c> before ending the session.
/// </para>
/// </summary>
public sealed class ScratchpadPlugin
{
    private readonly string _agentName;
    private readonly string _basePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ScratchpadPlugin(string agentName, string basePath)
    {
        _agentName = agentName;
        _basePath = FuseraftPaths.ExpandPath(basePath);
    }

    // Write

    [Description("Store a named entry in the scratchpad.")]
    public async Task<string> WriteAsync(
        [Description("Entry key.")]
        string key,
        [Description("Value to store.")]
        string value,
        [Description("Comma-separated tags.")]
        string? tags = null,
        [Description("Scope: 'agent' or 'global'.")]
        string scope = "agent")
    {
        if (string.IsNullOrWhiteSpace(key))
            return "[ERROR] Key must not be empty.";

        var data = await LoadAsync(scope, CancellationToken.None);
        data.Entries[key.Trim()] = new ScratchpadEntry
        {
            Value     = value,
            Tags      = string.IsNullOrWhiteSpace(tags) ? null : tags.Trim(),
            UpdatedAt = DateTime.UtcNow
        };
        await SaveAsync(scope, data, CancellationToken.None);
        return $"Saved '{key}' to scratchpad ({scope} scope).";
    }

    // Read

    [Description("Retrieve a scratchpad entry by key.")]
    public async Task<string> ReadAsync(
        [Description("Entry key.")]
        string key,
        [Description("Scope: 'agent' or 'global'.")]
        string scope = "agent")
    {
        var data = await LoadAsync(scope, CancellationToken.None);
        if (!data.Entries.TryGetValue(key.Trim(), out var entry))
            return $"[NOT FOUND] No entry for key '{key}' in {scope} scope.";

        return FormatEntry(key, entry);
    }

    [Description("Read all scratchpad entries.")]
    public async Task<string> ReadAllAsync(
        [Description("Scope: 'agent' or 'global'.")]
        string scope = "agent")
    {
        var data = await LoadAsync(scope, CancellationToken.None);
        if (data.Entries.Count == 0)
            return $"[EMPTY] No entries in {scope} scratchpad.";

        var sb = new StringBuilder();
        sb.AppendLine($"=== Scratchpad ({scope}: {(scope == "global" ? "global" : _agentName)}, {data.Entries.Count} entries) ===");

        foreach (var (key, entry) in data.Entries.OrderBy(e => e.Key))
        {
            sb.AppendLine();
            sb.Append($"[{key}]");
            if (entry.Tags is not null) sb.Append($"  tags: {entry.Tags}");
            sb.AppendLine($"  updated: {entry.UpdatedAt:yyyy-MM-dd HH:mm}");
            sb.AppendLine(entry.Value);
        }

        return sb.ToString().TrimEnd();
    }

    // Search

    [Description("Search scratchpad entries by key, value, or tag.")]
    public async Task<string> SearchAsync(
        [Description("Search query.")]
        string query,
        [Description("Scope: 'agent' or 'global'.")]
        string scope = "agent")
    {
        if (string.IsNullOrWhiteSpace(query))
            return "[ERROR] Query must not be empty.";

        var data = await LoadAsync(scope, CancellationToken.None);
        var matches = data.Entries
            .Where(kvp =>
                kvp.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                kvp.Value.Value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (kvp.Value.Tags?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        if (matches.Count == 0)
            return $"[NO RESULTS] No entries match '{query}' in {scope} scope.";

        var sb = new StringBuilder();
        sb.AppendLine($"=== Search results for '{query}' ({scope}: {(scope == "global" ? "global" : _agentName)}, {matches.Count} match(es)) ===");
        foreach (var (key, entry) in matches)
        {
            sb.AppendLine();
            sb.AppendLine(FormatEntry(key, entry));
        }
        return sb.ToString().TrimEnd();
    }

    // Delete

    [Description("Remove a scratchpad entry.")]
    public async Task<string> DeleteAsync(
        [Description("Entry key.")]
        string key,
        [Description("Scope: 'agent' or 'global'.")]
        string scope = "agent")
    {
        var data = await LoadAsync(scope, CancellationToken.None);
        if (!data.Entries.Remove(key.Trim()))
            return $"[NOT FOUND] No entry for key '{key}' in {scope} scope.";

        await SaveAsync(scope, data, CancellationToken.None);
        return $"Deleted '{key}' from scratchpad ({scope} scope).";
    }

    // Internal

    private string FilePath(string scope) =>
        Path.Combine(_basePath, scope == "global" ? "global.json" : $"{_agentName}.json");

    private static string FormatEntry(string key, ScratchpadEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append($"[{key}]");
        if (entry.Tags is not null) sb.Append($"  tags: {entry.Tags}");
        sb.AppendLine($"  updated: {entry.UpdatedAt:yyyy-MM-dd HH:mm}");
        sb.Append(entry.Value);
        return sb.ToString();
    }

    private async Task<ScratchpadData> LoadAsync(string scope, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var path = FilePath(scope);
            if (!File.Exists(path)) return new ScratchpadData();

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<ScratchpadData>(json, JsonOpts) ?? new ScratchpadData();
        }
        catch
        {
            return new ScratchpadData();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveAsync(string scope, ScratchpadData data, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var path = FilePath(scope);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var json = JsonSerializer.Serialize(data, JsonOpts);
            await File.WriteAllTextAsync(path, json, cancellationToken);

            // Restrict to owner read/write on non-Windows (scratchpad may contain sensitive agent notes).
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}

// DTOs

internal sealed class ScratchpadData
{
    [JsonPropertyName("entries")]
    public Dictionary<string, ScratchpadEntry> Entries { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record ScratchpadEntry
{
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("tags")]
    public string? Tags { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}
