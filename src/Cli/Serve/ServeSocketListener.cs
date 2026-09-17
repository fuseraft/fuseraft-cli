using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using fuseraft.Cli.Commands.Repl;

namespace fuseraft.Cli.Serve;

/// <summary>
/// The daemon's human front door: a Unix domain socket that <c>fuseraft attach</c> connects to.
/// Reuses the VS Code webview's JSONL protocol (<see cref="ReplStdinPump"/> verbatim; a small
/// per-connection sibling of <c>ReplJsonBridge</c> here since that class's output writer is a
/// process-wide static — see its doc comment — and this needs one per connection instead).
///
/// <para>
/// Any number of clients may attach concurrently. Every attached connection sees the daemon's
/// live progress broadcast (<see cref="ServeHumanApprovalService.BroadcastEvent"/>) regardless of
/// who dispatched the running task, but approval prompts are routed only to the task's own
/// originator (<see cref="ServeHumanApprovalService.SetCurrentTaskOrigin"/>) — a bystander
/// connection is never asked to approve a stranger's or an agent's action.
/// </para>
///
/// <para>
/// Per connection, task dispatch and approval-response reads share <see cref="ReplStdinPump"/>'s
/// single line channel exactly like the REPL does — so, like the REPL, the two must never be
/// awaited concurrently. The connection loop enforces this by blocking on
/// <see cref="ServeTaskRecord.Done"/> after dispatching a task (during which only an approval-gate
/// read is ever in flight on this pump) before reading the next input line — it does not read
/// the next task while the current one is still running.
/// </para>
/// </summary>
public sealed class ServeSocketListener(
    string socketPath,
    ServeTaskQueue queue,
    ServeHumanApprovalService approvalService,
    ILogger<ServeSocketListener> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private Socket? _listenSocket;

    public void Start(CancellationToken cancellationToken)
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath); // stale file from an unclean shutdown — the pidfile guard
                                      // already confirmed no live daemon owns it before we got here.
        var dir = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listenSocket.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listenSocket.Listen(backlog: 4);

        _ = AcceptLoopAsync(cancellationToken);
    }

    public void Stop()
    {
        try { _listenSocket?.Close(); } catch { /* best-effort */ }
        try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { /* best-effort */ }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await _listenSocket!.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Attach socket accept failed");
                continue;
            }

            _ = HandleConnectionAsync(accepted, ct);
        }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { AutoFlush = true };

            var pump = new ReplStdinPump(reader, getActiveCts: () => null);
            pump.Start();
            var sink = new SocketAttachSink(writer, pump);

            approvalService.RegisterConnection(sink);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var text = await pump.ReadInputAsync().ConfigureAwait(false);
                    if (text is null) break; // client disconnected
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var sessionId = queue.Enqueue(text, originSink: sink);
                    var record = queue.TryGet(sessionId)!;
                    sink.Emit(new { type = "queued", sessionId, position = queue.GetQueuePosition(sessionId) });

                    await record.Done.Task.ConfigureAwait(false);

                    sink.Emit(new
                    {
                        type = "result",
                        sessionId,
                        succeeded = record.Succeeded,
                        errorMessage = record.ErrorMessage,
                        lastAgent = record.LastAgent,
                        lastMessage = record.LastMessageExcerpt,
                    });
                }
            }
            finally
            {
                approvalService.UnregisterConnection(sink);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Attach connection ended abnormally");
        }
    }

    private sealed class SocketAttachSink(TextWriter writer, ReplStdinPump pump) : IServeAttachSink
    {
        // Reachable concurrently from two independent paths once broadcast is in play: the
        // connection's own approval-gate emit (from GateAsync, on this task's async flow) and
        // BroadcastEvent's forwarding of another task's live progress (from ServeHost's event
        // handlers). StreamWriter.WriteLine isn't safe under concurrent callers, so serialize them.
        private readonly Lock _writeLock = new();

        public void Emit(object payload)
        {
            var line = JsonSerializer.Serialize(payload, JsonOpts);
            lock (_writeLock) writer.WriteLine(line);
        }

        public Task<bool> ReadApprovalResponseAsync() => pump.ReadApprovalResponseAsync();
    }
}
