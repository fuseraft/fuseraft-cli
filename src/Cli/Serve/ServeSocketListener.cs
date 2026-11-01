using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Cli.Protocol;

namespace fuseraft.Cli.Serve;

/// <summary>
/// The daemon's human front door: a Unix domain socket that <c>fuseraft attach</c> connects to.
/// Reuses the VS Code webview's JSONL protocol (<see cref="ReplStdinPump"/> verbatim; a small
/// per-connection sibling of <c>ReplJsonBridge</c> here since that class's output writer is a
/// process-wide static — see its doc comment — and this needs one per connection instead).
///
/// <para>
/// Any number of clients may attach concurrently. A client is one connection, or, over the
/// WebSocket, every connection with the same client id (<see cref="ServeAttachClient"/>). Every attached connection sees the daemon's
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
/// <see cref="ServeAttachClient.WhenIdle"/> after dispatching a task (during which only an approval-gate
/// read is ever in flight on this pump) before reading the next input line — it does not read
/// the next task while the current one is still running.
/// </para>
/// </summary>
public sealed class ServeSocketListener(
    string socketPath,
    ServeTaskQueue queue,
    ServeHumanApprovalService approvalService,
    ILogger<ServeSocketListener> logger,
    TimeSpan? reconnectGrace = null)
{
    /// <summary>How long a task's approval waits for its client to reconnect, by default.</summary>
    public static readonly TimeSpan DefaultReconnectGrace = TimeSpan.FromSeconds(60);

    private readonly ServeAttachClients _clients = new(approvalService, reconnectGrace ?? DefaultReconnectGrace);
    private Socket? _listenSocket;

    /// <summary>The attach clients, by id.</summary>
    public ServeAttachClients Clients => _clients;

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
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        await HandleStreamAsync(stream, AttachIdentity.Anonymous, ct);
    }

    /// <summary>
    /// Runs one attach connection over any line stream; the WebSocket endpoint uses this too, with the
    /// connection's client id when it sent one.
    /// </summary>
    internal async Task HandleStreamAsync(Stream stream, AttachIdentity identity, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { AutoFlush = true };

            var pump = new ReplStdinPump(reader, getActiveCts: () => null);
            pump.Start();
            var connection = new AttachConnection(writer, pump, stream.Dispose);
            var client = _clients.Connect(identity, connection, queue.GetQueuePosition);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Only approval answers are read while this client's task runs (see the class comment);
                    // its result is sent by the client, to whichever connection it has by then. A connection
                    // that closes meanwhile is let go at once, so the client's reconnect grace can start.
                    if (await Task.WhenAny(client.WhenIdle(), pump.Closed).ConfigureAwait(false) == pump.Closed)
                        break;
                    var text = await pump.ReadInputAsync().ConfigureAwait(false);
                    if (text is null) break; // client disconnected
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (identity.ReadOnly)
                    {
                        connection.Emit(new { type = "error", text = "This token is read-only: it can watch the daemon, but not send it tasks." });
                        continue;
                    }

                    client.Dispatch(connection,
                        () => queue.TryGet(queue.Enqueue(text, originSink: client))!,
                        queue.GetQueuePosition);
                }
            }
            finally
            {
                _clients.Disconnect(client, connection);
            }
        }
        catch (IOException ex)
        {
            // The client went away mid-write: routine for a network client, so not worth a stack trace.
            logger.LogDebug(ex, "Attach connection dropped");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Attach connection ended abnormally");
        }
    }
}
