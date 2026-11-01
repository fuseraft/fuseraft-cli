using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Cli.Serve;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="ServeQueueJournal"/>: each test runs a "daemon" (a queue, its attach clients and a journal), then a
/// second one on the same file, as a restarted daemon would.
/// </summary>
public sealed class ServeQueueJournalTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-journal-").FullName;
    private readonly List<WebApplication> _apps = [];

    private string JournalPath => Path.Combine(_dir, "serve-queue.json");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _apps) await app.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Restore_RunsTheInterruptedTaskFirst_FromItsSession()
    {
        var first = Daemon();
        first.Journal.Restore();
        var a = first.Queue.Enqueue("first");
        var b = first.Queue.Enqueue("second");
        first.Queue.Start(first.Queue.TryGet(b)!);

        var second = Daemon();

        Assert.Equal(2, second.Journal.Restore());
        var resumed = await Next(second.Queue);
        Assert.Equal((b, true), (resumed.SessionId, resumed.Resume));
        var queued = await Next(second.Queue);
        Assert.Equal((a, false), (queued.SessionId, queued.Resume));
    }

    [Fact]
    public async Task AFinishedTask_IsNotRestored()
    {
        var first = Daemon();
        first.Journal.Restore();
        var id = first.Queue.Enqueue("done already");
        Finish(first.Queue.TryGet(id)!);

        await Eventually(() => !File.ReadAllText(JournalPath).Contains(id));
        Assert.Equal(0, Daemon().Journal.Restore());
    }

    [Fact]
    public async Task AClientThatReconnectsAfterARestart_GetsItsTaskAndItsResult()
    {
        var first = Daemon();
        first.Journal.Restore();
        var client = first.Clients.Restore(new AttachIdentity("web-1", "alice"));
        var id = first.Queue.Enqueue("deploy it", originSink: client);
        client.Track(first.Queue.TryGet(id)!);

        var second = Daemon();
        second.Journal.Restore();
        using var ws = await ConnectAsync(second, "web-1", tokenName: "alice");

        using (var hello = JsonDocument.Parse(await ReceiveAsync(ws)))
            Assert.Equal(id, hello.RootElement.GetProperty("inFlight").GetProperty("sessionId").GetString());
        var request = await Next(second.Queue);
        Assert.Same(second.Clients.Restore(new AttachIdentity("web-1", "alice")), request.OriginSink);
        Finish(second.Queue.TryGet(id)!);
        using var result = JsonDocument.Parse(await ReceiveAsync(ws));
        Assert.Equal(id, result.RootElement.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task AResultNobodyCollected_SurvivesARestart()
    {
        var first = Daemon();
        first.Journal.Restore();
        var client = first.Clients.Restore(new AttachIdentity("web-1"));
        var id = first.Queue.Enqueue("write the docs", originSink: client);
        client.Track(first.Queue.TryGet(id)!);
        Finish(first.Queue.TryGet(id)!);
        await Eventually(() => !File.ReadAllText(JournalPath).Contains("\"results\": []"));

        var second = Daemon();
        second.Journal.Restore();
        using var ws = await ConnectAsync(second, "web-1");

        using (var hello = JsonDocument.Parse(await ReceiveAsync(ws)))
            Assert.Equal(id, hello.RootElement.GetProperty("pendingResults")[0].GetString());
        using (var result = JsonDocument.Parse(await ReceiveAsync(ws)))
            Assert.Equal("write the docs", result.RootElement.GetProperty("task").GetString());
        // Delivered, so a third start doesn't send it again.
        await Eventually(() => !File.ReadAllText(JournalPath).Contains(id));
    }

    [Fact]
    public void ATaskThatKeepsInterruptingTheDaemon_IsFailedInsteadOfResumed()
    {
        File.WriteAllText(JournalPath, $$"""
            {"tasks":[{"sessionId":"abc12345","task":"crashy","running":true,"interruptions":{{ServeTaskQueue.MaxInterruptions}},"createdAt":"2026-11-01T00:00:00Z"}],"results":[]}
            """);
        var daemon = Daemon();

        daemon.Journal.Restore();

        var record = daemon.Queue.TryGet("abc12345")!;
        Assert.Equal(ServeTaskStatus.Failed, record.Status);
        Assert.Contains("isn't resumed again", record.ErrorMessage);
        Assert.False(daemon.Queue.Reader.TryRead(out _));
    }

    [Fact]
    public void AnUnreadableFile_IsSetAside()
    {
        File.WriteAllText(JournalPath, "{ not json");

        Assert.Equal(0, Daemon().Journal.Restore());

        Assert.True(File.Exists(JournalPath + ".corrupt"));
    }

    [Fact]
    public void TheFile_IsReadableOnlyByTheDaemonsUser()
    {
        if (OperatingSystem.IsWindows()) return;
        var daemon = Daemon();
        daemon.Journal.Restore();
        daemon.Queue.Enqueue("secret task");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(JournalPath));
    }

    private sealed record TestDaemon(ServeTaskQueue Queue, ServeSocketListener Listener, ServeQueueJournal Journal)
    {
        public ServeAttachClients Clients => Listener.Clients;
    }

    private TestDaemon Daemon()
    {
        var queue = new ServeTaskQueue();
        var listener = new ServeSocketListener("/unused.sock", queue, new ServeHumanApprovalService(false),
            NullLogger<ServeSocketListener>.Instance);
        return new TestDaemon(queue, listener, new ServeQueueJournal(JournalPath, queue, listener.Clients, NullLogger.Instance));
    }

    private async Task<ClientWebSocket> ConnectAsync(TestDaemon daemon, string clientId, string? tokenName = null)
    {
        var app = ServeHttpHost.Build(new ServeHttpOptions("127.0.0.1", 0, null), daemon.Queue,
            (stream, identity, ct) => daemon.Listener.HandleStreamAsync(stream, identity with { TokenName = tokenName }, ct));
        _apps.Add(app);
        await app.StartAsync();
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader(ServeAttachClients.ClientIdHeader, clientId);
        await ws.ConnectAsync(new Uri(app.Urls.First().Replace("http://", "ws://") + ServeHttpHost.AttachPath), default);
        return ws;
    }

    private static async Task<ServeTaskRequest> Next(ServeTaskQueue queue) =>
        await queue.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private static void Finish(ServeTaskRecord record)
    {
        record.Status    = ServeTaskStatus.Completed;
        record.Succeeded = true;
        record.Done.SetResult(true);
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[65536];
        var result = await ws.ReceiveAsync(buffer, default).WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }
}
