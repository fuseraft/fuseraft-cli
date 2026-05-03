using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Read-only plugin that exposes the session change log to agents.
///
/// The change log is written automatically by the orchestrator's <c>ChangeTracker</c>
/// after each agent turn. It records the tool calls that actually completed — files
/// written, commands run, git commits made — so downstream agents can observe what
/// happened without inferring it from the chat history.
///
/// Agents use this instead of asking "what did the Developer change?" — they just call
/// <c>changes_read_latest</c> and know exactly which files to test or review.
/// </summary>
public sealed class ChangesPlugin(string logPath)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Description("Get the full session change log.")]
    public async Task<string> ReadAsync()
    {
        var log = await LoadAsync(CancellationToken.None);
        if (log is null || log.Entries.Count == 0)
            return PluginResult.Info("No changes have been recorded yet this session.");

        return FormatLog(log.Entries);
    }

    [Description("Get the most recent change log entries.")]
    public async Task<string> ReadLatestAsync(
        [Description("Number of recent entries.")] int count = 1)
    {
        var log = await LoadAsync(CancellationToken.None);
        if (log is null || log.Entries.Count == 0)
            return PluginResult.Info("No changes have been recorded yet this session.");

        var entries = log.Entries.TakeLast(Math.Max(1, count)).ToList();
        return FormatLog(entries);
    }

    // Helpers

    private async Task<ChangeLog?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(logPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(logPath, ct);
            return JsonSerializer.Deserialize<ChangeLog>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatLog(IReadOnlyList<ChangeEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Changes Log ===");

        foreach (var e in entries)
        {
            sb.AppendLine();
            sb.AppendLine($"[Turn {e.TurnIndex}] {e.Agent}  ({e.Timestamp:yyyy-MM-dd HH:mm:ss} UTC)");

            if (e.FilesWritten.Count > 0)
            {
                sb.AppendLine("  Files written:");
                foreach (var f in e.FilesWritten) sb.AppendLine($"    - {f}");
            }

            if (e.FilesDeleted.Count > 0)
            {
                sb.AppendLine("  Files deleted:");
                foreach (var f in e.FilesDeleted) sb.AppendLine($"    - {f}");
            }

            if (e.CommandsRun.Count > 0)
            {
                sb.AppendLine("  Commands run:");
                foreach (var c in e.CommandsRun)
                    sb.AppendLine($"    - {c.Command}  [{(c.Succeeded ? "OK" : "FAILED")}]");
            }

            if (e.GitCommits.Count > 0)
            {
                sb.AppendLine("  Git commits:");
                foreach (var m in e.GitCommits) sb.AppendLine($"    - {m}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
