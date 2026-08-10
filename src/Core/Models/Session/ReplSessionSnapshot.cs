using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Core.Models.Session;

/// <summary>A single step in a /plan.</summary>
public sealed record PlanStep(
    int Step,
    string Description,
    string? Tool,
    string? Creates,
    string? Verifies  = null,
    int[]?  DependsOn = null)
{
    /// <summary>
    /// Extracts and parses the first JSON array of <see cref="PlanStep"/> objects found in
    /// <paramref name="text"/>. Returns true and populates <paramref name="steps"/> when a
    /// valid non-empty array is found; returns false otherwise.
    /// </summary>
    public static bool TryParse(string text, out PlanStep[] steps)
    {
        steps = [];
        var trimmed  = text.Trim();
        var startIdx = trimmed.IndexOf('[');
        var endIdx   = trimmed.LastIndexOf(']');
        if (startIdx < 0 || endIdx <= startIdx) return false;
        var json = trimmed[startIdx..(endIdx + 1)];
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            steps = JsonSerializer.Deserialize<PlanStep[]>(json, opts) ?? [];
            return steps.Length > 0;
        }
        catch { return false; }
    }
}

/// <summary>A queue entry pairing a step with the total step count for display.</summary>
public sealed record PlanStepEntry(PlanStep Step, int Total);

/// <summary>
/// Snapshot of a REPL session written to disk after every user turn so the session can be resumed.
/// </summary>
public sealed record ReplSessionSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented              = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
    };

    public required string SessionId { get; init; }
    public required string ModelId   { get; init; }
    public required string Cwd       { get; init; }

    public DateTime StartedAt     { get; init; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set;  } = DateTime.UtcNow;
    public int      TurnIndex     { get; set;  }

    public List<ReplSerializedMessage> History { get; init; } = [];

    // Plan execution state — persisted so a crash mid-plan can be recovered on --resume.
    public PlanStep[]?      PendingPlan     { get; init; }
    public PlanStepEntry[]? ExecutionQueue  { get; init; }
    public PlanStepEntry?   HaltedAt        { get; init; }
    public PlanStepEntry[]? HaltedRemaining { get; init; }
    public string[]?        HaltedToolCalls { get; init; }
    public string?          RecoveryHint    { get; init; }

    // Self-directed todo list state (see TodoPlugin) — persisted so /resume doesn't leave
    // todo_read contradicting the restored chat history's last todo_write call.
    public TodoItem[]?      TodoItems       { get; init; }

    // -------------------------------------------------------------------------

    public static ReplSessionSnapshot Capture(
        string sessionId, string modelId, string cwd,
        int turnIndex, IReadOnlyList<ChatMessage> history, DateTime startedAt,
        PlanStep[]?      currentPlan     = null,
        PlanStepEntry[]? executionQueue  = null,
        PlanStepEntry?   haltedAt        = null,
        PlanStepEntry[]? haltedRemaining = null,
        string[]?        haltedToolCalls = null,
        string?          recoveryHint    = null,
        TodoItem[]?      todoItems       = null) => new()
    {
        SessionId       = sessionId,
        ModelId         = modelId,
        Cwd             = cwd,
        StartedAt       = startedAt,
        TurnIndex       = turnIndex,
        History         = [.. history.Select(ReplSerializedMessage.From)],
        PendingPlan     = currentPlan,
        ExecutionQueue  = executionQueue,
        HaltedAt        = haltedAt,
        HaltedRemaining = haltedRemaining,
        HaltedToolCalls = haltedToolCalls,
        RecoveryHint    = recoveryHint,
        TodoItems       = todoItems,
    };

    /// <summary>Restores the serialized history as live ChatMessage objects.</summary>
    public List<ChatMessage> RestoreHistory() =>
        [.. History
            .Select(m => m.Restore())
            .Where(m => m is not null)
            .Cast<ChatMessage>()];

    // -------------------------------------------------------------------------
    // Store operations
    // -------------------------------------------------------------------------

    public static async Task SaveAsync(ReplSessionSnapshot snapshot, CancellationToken ct = default)
    {
        var dir = FuseraftPaths.GlobalReplSessions;
        Directory.CreateDirectory(dir);
        snapshot.LastUpdatedAt = DateTime.UtcNow;
        var path = SnapshotPath(snapshot.SessionId);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, ct);
        await stream.FlushAsync(ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static async Task<ReplSessionSnapshot?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        var path = SnapshotPath(sessionId);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<ReplSessionSnapshot>(stream, JsonOptions, ct);
    }

    public static async Task<IReadOnlyList<ReplSessionSnapshot>> ListAsync(CancellationToken ct = default)
    {
        var dir = FuseraftPaths.GlobalReplSessions;
        if (!Directory.Exists(dir)) return [];
        var files = Directory.GetFiles(dir, "repl-*.json");
        var results = new List<ReplSessionSnapshot>(files.Length);
        foreach (var file in files)
        {
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var snap = await JsonSerializer.DeserializeAsync<ReplSessionSnapshot>(stream, JsonOptions, ct);
                if (snap is not null) results.Add(snap);
            }
            catch { }
        }
        results.Sort((a, b) => b.LastUpdatedAt.CompareTo(a.LastUpdatedAt));
        return results;
    }

    private static string SnapshotPath(string sessionId) =>
        Path.Combine(FuseraftPaths.GlobalReplSessions, $"repl-{sessionId}.json");
}

/// <summary>JSON-serializable form of a ChatMessage.</summary>
public sealed record ReplSerializedMessage
{
    public string Role { get; init; } = "";
    public List<ReplSerializedContent> Contents { get; init; } = [];

    public static ReplSerializedMessage From(ChatMessage msg) => new()
    {
        Role     = msg.Role.Value,
        Contents = [.. msg.Contents.Select(ReplSerializedContent.From)],
    };

    public ChatMessage? Restore()
    {
        var role     = new ChatRole(Role);
        var contents = Contents
            .Select(c => c.Restore())
            .Where(c => c is not null)
            .Cast<AIContent>()
            .ToList();
        return contents.Count > 0 ? new ChatMessage(role, contents) : null;
    }
}

/// <summary>JSON-serializable form of a single AIContent item.</summary>
public sealed record ReplSerializedContent
{
    public string  Type          { get; init; } = "text";
    public string? Text          { get; init; }
    public string? CallId        { get; init; }
    public string? FunctionName  { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? ResultJson    { get; init; }

    public static ReplSerializedContent From(AIContent content)
    {
        if (content is TextContent tc)
            return new() { Type = "text", Text = tc.Text };

        if (content is FunctionCallContent fc)
        {
            string? argsJson = null;
            try { if (fc.Arguments is not null) argsJson = JsonSerializer.Serialize(fc.Arguments); }
            catch { }
            return new()
            {
                Type          = "function_call",
                CallId        = fc.CallId,
                FunctionName  = fc.Name,
                ArgumentsJson = argsJson,
            };
        }

        if (content is FunctionResultContent fr)
        {
            string? resultJson = null;
            try { if (fr.Result is not null) resultJson = JsonSerializer.Serialize(fr.Result); }
            catch { }
            return new()
            {
                Type       = "function_result",
                CallId     = fr.CallId,
                ResultJson = resultJson,
            };
        }

        return new() { Type = "skip" };
    }

    public AIContent? Restore() => Type switch
    {
        "text"            => new TextContent(Text ?? ""),
        "function_call"   => RestoreFunctionCall(),
        "function_result" => new FunctionResultContent(CallId ?? "", RestoreResult()),
        _                 => null,
    };

    private FunctionCallContent RestoreFunctionCall()
    {
        IDictionary<string, object?>? args = null;
        if (ArgumentsJson is not null)
        {
            try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(ArgumentsJson); }
            catch { }
        }
        return new FunctionCallContent(CallId ?? "", FunctionName ?? "", args);
    }

    private object? RestoreResult()
    {
        if (ResultJson is null) return null;
        try { return JsonSerializer.Deserialize<object>(ResultJson); }
        catch { return ResultJson; }
    }
}
