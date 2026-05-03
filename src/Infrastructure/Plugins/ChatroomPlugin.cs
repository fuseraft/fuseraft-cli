using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Per-agent plugin that lets agents send and receive coordination messages through a
/// shared append-only JSONL log.
///
/// <para>
/// Each agent gets its own instance bound to its name, but all instances share the same
/// file on disk so messages are visible to all agents in the session.
/// </para>
/// </summary>
public sealed class ChatroomPlugin
{
    private readonly string _agentName;
    private readonly string _chatPath;

    // One lock per file path — prevents interleaved writes when agents run concurrently.
    private static readonly Dictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lockMapGuard = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ChatroomPlugin(string agentName, string chatPath)
    {
        _agentName = agentName;
        _chatPath  = chatPath.Replace(
            "~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            StringComparison.Ordinal);
    }

    // Send

    [Description("Send a message to another agent or 'All'.")]
    public async Task<string> SendAsync(
        [Description("Recipient agent name or 'All'.")]
        string recipient,
        [Description("Message content.")]
        string message)
    {
        if (string.IsNullOrWhiteSpace(recipient))
            return "[ERROR] Recipient must not be empty.";
        if (string.IsNullOrWhiteSpace(message))
            return "[ERROR] Message must not be empty.";

        var entry = new ChatroomEntry
        {
            Timestamp = DateTime.UtcNow,
            From      = _agentName,
            To        = recipient.Trim(),
            Message   = message.Trim()
        };

        var line = JsonSerializer.Serialize(entry, JsonOpts);

        var fileLock = GetLock(_chatPath);
        await fileLock.WaitAsync(CancellationToken.None);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_chatPath)!);
            await File.AppendAllTextAsync(_chatPath, line + "\n", CancellationToken.None);
        }
        finally
        {
            fileLock.Release();
        }

        return $"Message sent to {entry.To}.";
    }

    // Read

    [Description("Read recent messages from the shared chatroom.")]
    public async Task<string> ReadAsync(
        [Description("Number of recent messages.")]
        int count = 20)
    {
        if (!File.Exists(_chatPath))
            return "[EMPTY] No chatroom messages yet.";

        string[] lines;
        var fileLock = GetLock(_chatPath);
        await fileLock.WaitAsync(CancellationToken.None);
        try
        {
            lines = await File.ReadAllLinesAsync(_chatPath, CancellationToken.None);
        }
        finally
        {
            fileLock.Release();
        }

        var recent = lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(Math.Max(1, count))
            .ToList();

        if (recent.Count == 0)
            return "[EMPTY] No chatroom messages yet.";

        var sb = new StringBuilder();
        sb.AppendLine($"=== Chatroom (last {recent.Count} message(s)) ===");

        foreach (var line in recent)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<ChatroomEntry>(line, JsonOpts);
                if (entry is null) continue;

                sb.AppendLine();
                sb.AppendLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss} UTC]  {entry.From} → {entry.To}");
                sb.AppendLine(entry.Message);
            }
            catch
            {
                // Skip malformed lines without breaking the read.
            }
        }

        return sb.ToString().TrimEnd();
    }

    // Helpers

    private static SemaphoreSlim GetLock(string path)
    {
        lock (_lockMapGuard)
        {
            if (!_locks.TryGetValue(path, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _locks[path] = sem;
            }
            return sem;
        }
    }
}

// DTO

internal sealed class ChatroomEntry
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }

    [JsonPropertyName("from")]
    public string From { get; init; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
