using System.Text.Json;
using System.Text.RegularExpressions;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Cli.Protocol;

namespace fuseraft.Cli.Serve;

/// <summary>
/// Who an attach connection says it is: the client id it sent, and the name and scope of the token it authenticated
/// with. Ids are kept per token, so a client holding one token can't take over another token's client.
/// </summary>
/// <param name="ReadOnly">The client watches only: the daemon refuses its tasks, so nothing asks it for approval.</param>
public sealed record AttachIdentity(string? ClientId, string? TokenName = null, bool ReadOnly = false)
{
    public static AttachIdentity Anonymous { get; } = new(ClientId: null);

    internal string? Key => ClientId is null ? null : TokenName is null ? ClientId : $"{TokenName}/{ClientId}";
}

/// <summary>One attach connection: its writer, the pump that reads its lines, and a way to close it.</summary>
internal sealed class AttachConnection(TextWriter writer, ReplStdinPump pump, Action close)
{
    // A broadcast, an approval request and a result can be written from different flows at once.
    private readonly Lock _writeLock = new();
    private readonly TaskCompletionSource _detached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ReplStdinPump Pump => pump;

    /// <summary>Completes once the connection's handler has finished with it.</summary>
    public Task Detached => _detached.Task;

    public void MarkDetached() => _detached.TrySetResult();

    public void Emit(object payload)
    {
        var line = JsonSerializer.Serialize(payload, WireProtocol.JsonOpts);
        lock (_writeLock) writer.WriteLine(line);
    }

    public void Close()
    {
        try { close(); } catch { /* already closed */ }
    }
}

/// <summary>
/// An attach client as the daemon sees it. An anonymous client is one connection. A client that names an id
/// (the <c>Fuseraft-Client-Id</c> header) is every connection with that id, so when it reconnects within the
/// grace period it still answers its running task's approvals, and gets the results that finished while it was
/// away. Tasks keep the client, not the connection, as their origin.
/// </summary>
public sealed class ServeAttachClient : IServeAttachSink
{
    private const int MaxUndeliveredResults = 20;

    private readonly Lock _lock = new();
    private readonly TimeSpan _grace;
    private readonly List<(string SessionId, JsonElement Result)> _undelivered = [];
    private AttachConnection? _connection;
    // Oldest first. A connection dispatches one task at a time, but a restored client can have more.
    private readonly List<ServeTaskRecord> _inFlight = [];
    private TaskCompletionSource _attached = NewSignal();
    private TaskCompletionSource _idle = NewSignal();
    private bool _ended;

    internal ServeAttachClient(AttachIdentity identity, TimeSpan grace)
    {
        Identity = identity;
        _grace   = identity.ClientId is null ? TimeSpan.Zero : grace;
        _idle.SetResult();
    }

    public AttachIdentity Identity { get; }

    /// <summary>Null for an anonymous client, which ends with its connection.</summary>
    public string? Id => Identity.ClientId;

    internal string? Key => Identity.Key;

    internal DateTimeOffset? DisconnectedAt { get; private set; }

    internal bool IsConnected
    {
        get { lock (_lock) return _connection is not null; }
    }

    /// <summary>Raised when the results waiting for this client change.</summary>
    internal event Action? UndeliveredChanged;

    /// <summary>The results that finished while this client was away, oldest first.</summary>
    internal IReadOnlyList<(string SessionId, JsonElement Result)> Undelivered
    {
        get { lock (_lock) return [.. _undelivered]; }
    }

    /// <summary>Live progress: dropped while the client isn't connected.</summary>
    public void Emit(object payload)
    {
        AttachConnection? connection;
        lock (_lock) connection = _connection;
        connection?.Emit(payload);
    }

    public async Task<bool> RequestApprovalAsync(object request)
    {
        while (true)
        {
            var connection = await WaitForConnectionAsync()
                ?? throw new IOException("The client that dispatched this task is no longer connected.");
            try
            {
                connection.Emit(request);
                if (await connection.Pump.TryReadApprovalResponseAsync() is { } approved)
                    return approved;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }

            // The connection closed before answering; ask again on the next one. Its own
            // handler detaches it, so wait for that rather than asking the same connection again.
            connection.Close();
            await connection.Detached;
        }
    }

    /// <summary>The client's current connection, waiting out the grace period for one; null once that's over.</summary>
    private async Task<AttachConnection?> WaitForConnectionAsync()
    {
        while (true)
        {
            Task attached;
            TimeSpan remaining;
            lock (_lock)
            {
                if (_connection is not null) return _connection;
                if (_ended) return null;
                remaining = (DisconnectedAt ?? DateTimeOffset.UtcNow) + _grace - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) return null;
                attached = _attached.Task;
            }
            if (await Task.WhenAny(attached, Task.Delay(remaining)) != attached) return null;
        }
    }

    /// <summary>
    /// Makes <paramref name="connection"/> the client's current one, closing any it replaces. The hello, then any
    /// undelivered results, are its first lines.
    /// </summary>
    internal void Attach(AttachConnection connection, Func<string, int?> queuePosition)
    {
        AttachConnection? replaced;
        bool hadUndelivered;
        lock (_lock)
        {
            replaced       = _connection;
            _connection    = connection;
            DisconnectedAt = null;

            var inFlight = _inFlight.FirstOrDefault() is { } r
                ? new { sessionId = r.SessionId, task = r.Task, status = r.Status.ToString().ToLowerInvariant(), position = queuePosition(r.SessionId) }
                : null;
            connection.Emit(new
            {
                type            = "hello",
                protocolVersion = WireProtocol.Version,
                clientId        = Id,
                readOnly        = Identity.ReadOnly,
                inFlight,
                pendingResults  = _undelivered.Select(u => u.SessionId).ToArray(),
            });
            foreach (var (_, result) in _undelivered) connection.Emit(result);
            hadUndelivered = _undelivered.Count > 0;
            _undelivered.Clear();

            var attached = _attached;
            _attached = NewSignal();
            attached.SetResult();
        }
        replaced?.Close();
        if (hadUndelivered) UndeliveredChanged?.Invoke();
    }

    /// <summary>A client the daemon had before it restarted: away since now, with the results it hadn't collected.</summary>
    internal void Restore(IEnumerable<(string SessionId, JsonElement Result)> undelivered)
    {
        lock (_lock)
        {
            DisconnectedAt ??= DateTimeOffset.UtcNow;
            _undelivered.AddRange(undelivered);
        }
    }

    /// <summary>Follows a restored task this client dispatched, as <see cref="Dispatch"/> does for a new one.</summary>
    internal void Track(ServeTaskRecord record)
    {
        lock (_lock)
        {
            _inFlight.Add(record);
            if (_idle.Task.IsCompleted) _idle = NewSignal();
            _ = record.Done.Task.ContinueWith(_ => Finished(record), TaskScheduler.Default);
        }
    }

    /// <summary>Forgets <paramref name="connection"/> if it's still the current one; true when the client can be dropped.</summary>
    internal bool Detach(AttachConnection connection)
    {
        lock (_lock)
        {
            if (_connection == connection)
            {
                _connection    = null;
                DisconnectedAt = DateTimeOffset.UtcNow;
                if (Id is null) _ended = true;
            }
            else if (_connection is not null) return false;
            return _ended || (_inFlight.Count == 0 && _undelivered.Count == 0);
        }
    }

    /// <summary>Enqueues a task for this client and tells it the task was queued, before anything else about the task.</summary>
    internal void Dispatch(AttachConnection connection, Func<ServeTaskRecord> enqueue, Func<string, int?> queuePosition)
    {
        ServeTaskRecord record;
        lock (_lock)
        {
            record    = enqueue();
            _inFlight.Add(record);
            if (_idle.Task.IsCompleted) _idle = NewSignal();
            _ = record.Done.Task.ContinueWith(_ => Finished(record), TaskScheduler.Default);
            // A closed connection shows up on its next read; the task runs either way.
            try { connection.Emit(new { type = "queued", sessionId = record.SessionId, position = queuePosition(record.SessionId) }); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    /// <summary>Completes once no task this client dispatched is queued or running.</summary>
    internal Task WhenIdle()
    {
        lock (_lock) return _idle.Task;
    }

    private void Finished(ServeTaskRecord record)
    {
        var result = JsonSerializer.SerializeToElement(new
        {
            type         = "result",
            sessionId    = record.SessionId,
            task         = record.Task,
            succeeded    = record.Succeeded,
            errorMessage = record.ErrorMessage,
            lastAgent    = record.LastAgent,
            lastMessage  = record.LastMessageExcerpt,
        }, WireProtocol.JsonOpts);
        TaskCompletionSource? idle;
        var queued = false;
        lock (_lock)
        {
            var delivered = false;
            if (_connection is { } connection)
            {
                try
                {
                    connection.Emit(result);
                    delivered = true;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
            if (!delivered && Id is not null)
            {
                _undelivered.Add((record.SessionId, result));
                if (_undelivered.Count > MaxUndeliveredResults) _undelivered.RemoveAt(0);
                queued = true;
            }
            _inFlight.Remove(record);
            idle = _idle;
            if (_inFlight.Count > 0) idle = null;
        }
        idle?.TrySetResult();
        if (queued) UndeliveredChanged?.Invoke();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The daemon's attach clients, by id, so a reconnecting client finds the one it had.</summary>
public sealed partial class ServeAttachClients(ServeHumanApprovalService approvals, TimeSpan reconnectGrace)
{
    public const string ClientIdHeader = "Fuseraft-Client-Id";

    // How long a client that left results nobody collected is remembered, and how many such clients at most.
    private static readonly TimeSpan RetainDisconnected = TimeSpan.FromHours(24);
    private const int MaxDisconnected = 100;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ServeAttachClient> _clients = new(StringComparer.Ordinal);

    public TimeSpan ReconnectGrace => reconnectGrace;

    /// <summary>A usable client id: 1–64 letters, digits, <c>-</c> or <c>_</c>.</summary>
    public static bool IsValidId(string id) => ValidId().IsMatch(id);

    /// <summary>Raised when the results waiting for any client change, for <see cref="ServeQueueJournal"/>.</summary>
    public event Action? Changed;

    /// <summary>Every named client that has results waiting.</summary>
    internal IReadOnlyList<ServeAttachClient> WithUndelivered
    {
        get { lock (_lock) return [.. _clients.Values.Where(c => c.Undelivered.Count > 0)]; }
    }

    /// <summary>The client <paramref name="identity"/> names, made away if it's new: one the daemon had before it restarted.</summary>
    internal ServeAttachClient Restore(AttachIdentity identity)
    {
        var key = identity.Key ?? throw new ArgumentException("Only a named client can be restored.", nameof(identity));
        lock (_lock)
        {
            if (_clients.TryGetValue(key, out var existing)) return existing;
            var client = Add(identity);
            client.Restore([]);
            return client;
        }
    }

    private ServeAttachClient Add(AttachIdentity identity)
    {
        var client = new ServeAttachClient(identity, reconnectGrace);
        if (identity.Key is { } key) _clients[key] = client;
        client.UndeliveredChanged += () => Changed?.Invoke();
        approvals.RegisterConnection(client);
        return client;
    }

    internal ServeAttachClient Connect(AttachIdentity identity, AttachConnection connection, Func<string, int?> queuePosition)
    {
        ServeAttachClient client;
        bool swept;
        lock (_lock)
        {
            swept = Sweep();
            if (identity.Key is not { } key || !_clients.TryGetValue(key, out client!))
                client = Add(identity);
        }
        if (swept) Changed?.Invoke();
        client.Attach(connection, queuePosition);
        return client;
    }

    internal void Disconnect(ServeAttachClient client, AttachConnection connection)
    {
        var droppable = client.Detach(connection);
        connection.MarkDetached();
        if (!droppable) return;
        lock (_lock)
        {
            if (client.Key is { } key && _clients.GetValueOrDefault(key) == client) _clients.Remove(key);
        }
        approvals.UnregisterConnection(client);
    }

    /// <summary>
    /// Drops clients that left results nobody came back for: after a day, or the oldest beyond
    /// <see cref="MaxDisconnected"/>. A client that reconnects after that starts over. True when it dropped any.
    /// </summary>
    private bool Sweep()
    {
        var swept = false;
        var gone = _clients.Values
            .Where(c => !c.IsConnected && c.DisconnectedAt is not null)
            .OrderBy(c => c.DisconnectedAt)
            .ToList();
        for (var i = 0; i < gone.Count; i++)
        {
            if (i >= gone.Count - MaxDisconnected && DateTimeOffset.UtcNow - gone[i].DisconnectedAt!.Value <= RetainDisconnected)
                continue;
            _clients.Remove(gone[i].Key!);
            approvals.UnregisterConnection(gone[i]);
            swept = true;
        }
        return swept;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidId();
}
