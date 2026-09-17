using System.Collections.Concurrent;
using System.Threading.Channels;
using fuseraft.Core.Models.Agents;

namespace fuseraft.Cli.Serve;

/// <summary>
/// Lifecycle of a dispatched task, tracked in <see cref="ServeTaskRecord.Status"/>.
/// </summary>
public enum ServeTaskStatus
{
    Queued,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// One dispatched task's request payload — enqueued by <c>dispatch_task</c> (MCP) or a
/// human-attach <c>input</c> message, consumed by <see cref="ServeHost"/>'s single worker loop.
/// </summary>
/// <param name="OriginSink">
/// The attach connection that dispatched this task, if any — null for an MCP-dispatched task.
/// Used by <c>ServeHost</c> to scope mutating-action approval prompts to whoever actually asked
/// for this task, rather than to any bystander who merely happens to be attached.
/// </param>
public sealed record ServeTaskRequest(string SessionId, string Task, string? ObjectiveId, IServeAttachSink? OriginSink);

/// <summary>
/// Mutable status/result record for one dispatched task, keyed by session ID. The worker loop
/// is the only writer; <c>get_status</c>/<c>get_result</c> (and the attach socket) are readers —
/// no locking needed since fuseraft's orchestrators only ever process one task at a time (see
/// <see cref="ServeTaskQueue"/>'s serialization guarantee).
/// </summary>
public sealed class ServeTaskRecord(string sessionId, string task, string? objectiveId, IServeAttachSink? originSink)
{
    public string SessionId { get; } = sessionId;
    public string Task { get; } = task;
    public string? ObjectiveId { get; } = objectiveId;
    public IServeAttachSink? OriginSink { get; } = originSink;
    public ServeTaskStatus Status { get; set; } = ServeTaskStatus.Queued;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool? Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
    public int TurnCount { get; set; }
    public string? LastAgent { get; set; }
    public string? LastMessageExcerpt { get; set; }
    public List<AgentMessage>? Messages { get; set; }

    /// <summary>
    /// Completed by <see cref="ServeHost"/>'s worker loop once this task reaches a terminal
    /// state. A socket-attached caller that dispatched this task awaits it to know when to
    /// report the result, without racing <see cref="ServeSocketListener"/>'s own input-reading
    /// loop against approval-response reads on the same connection (see that class for why the
    /// two must never read concurrently).
    /// </summary>
    public TaskCompletionSource<bool> Done { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// The daemon's single task queue. All dispatched tasks — whether from the MCP <c>dispatch_task</c>
/// tool or a human attached over the socket — funnel through here and are processed one at a time
/// by <see cref="ServeHost"/>'s worker loop, since every fuseraft orchestrator assumes one shared,
/// mutable conversation history (see docs/design.md §6.1) and cannot safely run two tasks
/// concurrently against the same orchestrator instance.
///
/// Blocking on <see cref="ChannelReader{T}.ReadAsync(CancellationToken)"/> when the queue is
/// empty <em>is</em> the daemon's idle mode.
/// </summary>
public sealed class ServeTaskQueue
{
    private readonly Channel<ServeTaskRequest> _channel = Channel.CreateUnbounded<ServeTaskRequest>();
    private readonly ConcurrentDictionary<string, ServeTaskRecord> _records = new();

    // Caps how many *terminal* (Completed/Failed) records stay in memory. Without this, a
    // daemon left running for days under --auto-objective (a supported, documented use case —
    // see docs/serve.md) would accumulate one full conversation-history record per completed
    // task forever, on a process explicitly designed to stay resident indefinitely. Long-term
    // history already survives durably via the session store; this dictionary only needs to
    // hold enough recent results for get_status/get_result to still answer for them. Queued and
    // Running records are never evicted regardless of count.
    private const int MaxRetainedTerminalRecords = 500;

    public ChannelReader<ServeTaskRequest> Reader => _channel.Reader;

    /// <summary>
    /// Enqueues a new task and returns its freshly generated session ID immediately —
    /// the task itself runs later, whenever the worker loop reaches it.
    /// </summary>
    public string Enqueue(string task, string? objectiveId = null, IServeAttachSink? originSink = null)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        _records[sessionId] = new ServeTaskRecord(sessionId, task, objectiveId, originSink);
        // Unbounded channel — TryWrite never fails.
        _channel.Writer.TryWrite(new ServeTaskRequest(sessionId, task, objectiveId, originSink));
        EvictOldestTerminalRecords();
        return sessionId;
    }

    private void EvictOldestTerminalRecords()
    {
        var terminal = _records.Values
            .Where(r => r.Status is ServeTaskStatus.Completed or ServeTaskStatus.Failed)
            .ToList();
        var overflow = terminal.Count - MaxRetainedTerminalRecords;
        if (overflow <= 0) return;

        foreach (var r in terminal.OrderBy(r => r.CompletedAt ?? r.CreatedAt).Take(overflow))
            _records.TryRemove(r.SessionId, out _);
    }

    public ServeTaskRecord? TryGet(string sessionId) =>
        _records.GetValueOrDefault(sessionId);

    /// <summary>
    /// Number of tasks ahead of <paramref name="sessionId"/> — running, plus queued earlier —
    /// or <c>null</c> once it's no longer queued (already running, completed, or failed) or
    /// unknown. <c>0</c> means it will run next.
    /// </summary>
    public int? GetQueuePosition(string sessionId)
    {
        var record = TryGet(sessionId);
        if (record is null || record.Status != ServeTaskStatus.Queued) return null;

        return _records.Values.Count(r =>
            r.Status == ServeTaskStatus.Running ||
            (r.Status == ServeTaskStatus.Queued && r.CreatedAt < record.CreatedAt));
    }
}
