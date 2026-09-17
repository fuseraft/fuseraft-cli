using fuseraft.Cli.Serve;

namespace FuseraftCli.Tests;

/// <summary>
/// Covers <see cref="ServeTaskQueue"/>'s enqueue/lookup contract — the single-worker-loop
/// serialization guarantee itself is exercised end-to-end by <c>ServeHost</c> rather than here,
/// since it depends on a real orchestrator.
/// </summary>
public sealed class ServeTaskQueueTests
{
    [Fact]
    public void Enqueue_CreatesARecord_QueuedByDefault()
    {
        var queue = new ServeTaskQueue();

        var sessionId = queue.Enqueue("do the thing");
        var record = queue.TryGet(sessionId);

        Assert.NotNull(record);
        Assert.Equal(ServeTaskStatus.Queued, record!.Status);
        Assert.Equal("do the thing", record.Task);
        Assert.Null(record.ObjectiveId);
    }

    [Fact]
    public void Enqueue_WithObjectiveId_IsCarriedOnTheRecordAndRequest()
    {
        var queue = new ServeTaskQueue();

        var sessionId = queue.Enqueue("do the thing", objectiveId: "OBJ-0001");

        Assert.Equal("OBJ-0001", queue.TryGet(sessionId)!.ObjectiveId);
        Assert.True(queue.Reader.TryRead(out var request));
        Assert.Equal("OBJ-0001", request.ObjectiveId);
        Assert.Equal(sessionId, request.SessionId);
    }

    [Fact]
    public void TryGet_UnknownSessionId_ReturnsNull()
    {
        var queue = new ServeTaskQueue();
        Assert.Null(queue.TryGet("does-not-exist"));
    }

    private sealed class FakeSink : IServeAttachSink
    {
        public void Emit(object payload) { }
        public Task<bool> ReadApprovalResponseAsync() => Task.FromResult(true);
    }

    [Fact]
    public void Enqueue_WithOriginSink_IsCarriedOnTheRecordAndRequest()
    {
        var queue = new ServeTaskQueue();
        var sink = new FakeSink();

        var sessionId = queue.Enqueue("do the thing", originSink: sink);

        Assert.Same(sink, queue.TryGet(sessionId)!.OriginSink);
        Assert.True(queue.Reader.TryRead(out var request));
        Assert.Same(sink, request.OriginSink);
    }

    [Fact]
    public void Enqueue_WithoutOriginSink_LeavesItNull()
    {
        var queue = new ServeTaskQueue();
        var sessionId = queue.Enqueue("do the thing");
        Assert.Null(queue.TryGet(sessionId)!.OriginSink);
    }

    [Fact]
    public void GetQueuePosition_FirstTask_IsZero()
    {
        var queue = new ServeTaskQueue();
        var sessionId = queue.Enqueue("first");
        Assert.Equal(0, queue.GetQueuePosition(sessionId));
    }

    [Fact]
    public void GetQueuePosition_CountsEarlierQueuedTasks()
    {
        var queue = new ServeTaskQueue();
        queue.Enqueue("first");
        queue.Enqueue("second");
        var third = queue.Enqueue("third");

        Assert.Equal(2, queue.GetQueuePosition(third));
    }

    [Fact]
    public void GetQueuePosition_CountsARunningTaskAheadOfIt()
    {
        var queue = new ServeTaskQueue();
        var running = queue.TryGet(queue.Enqueue("running"))!;
        running.Status = ServeTaskStatus.Running;
        var waiting = queue.Enqueue("waiting");

        Assert.Equal(1, queue.GetQueuePosition(waiting));
    }

    [Fact]
    public void GetQueuePosition_NullOnceNoLongerQueued()
    {
        var queue = new ServeTaskQueue();
        var record = queue.TryGet(queue.Enqueue("do the thing"))!;

        record.Status = ServeTaskStatus.Running;
        Assert.Null(queue.GetQueuePosition(record.SessionId));

        record.Status = ServeTaskStatus.Completed;
        Assert.Null(queue.GetQueuePosition(record.SessionId));
    }

    [Fact]
    public void GetQueuePosition_UnknownSessionId_ReturnsNull()
    {
        var queue = new ServeTaskQueue();
        Assert.Null(queue.GetQueuePosition("does-not-exist"));
    }

    [Fact]
    public void Reader_DeliversRequestsInFifoOrder()
    {
        var queue = new ServeTaskQueue();
        var first  = queue.Enqueue("first");
        var second = queue.Enqueue("second");

        Assert.True(queue.Reader.TryRead(out var a));
        Assert.True(queue.Reader.TryRead(out var b));
        Assert.Equal(first,  a.SessionId);
        Assert.Equal(second, b.SessionId);
    }

    [Fact]
    public async Task Done_CompletesOnlyAfterExplicitlySet()
    {
        var queue = new ServeTaskQueue();
        var sessionId = queue.Enqueue("do the thing");
        var record = queue.TryGet(sessionId)!;

        var completedEarly = record.Done.Task.IsCompleted;
        record.Done.TrySetResult(true);
        await record.Done.Task; // must not hang

        Assert.False(completedEarly);
    }
}
