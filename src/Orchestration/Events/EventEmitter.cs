using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration.Events;

/// <summary>
/// Appends structured JSONL events to a file — one JSON object per line — and dispatches
/// each event to any registered <see cref="IOrchestrationHook"/> implementations.
///
/// Schema: <c>{ ts, session, agent, turn, event_type, payload }</c>
///
/// <para>
/// All event type strings are defined as constants in <see cref="EventTypes"/>.
/// </para>
///
/// <para>
/// All file writes are serialized through a <see cref="SemaphoreSlim"/> so concurrent agent
/// turns cannot interleave partial lines. Hook invocations happen after the file write and
/// outside the lock so they do not block other emitters. All errors — both file I/O and hook
/// exceptions — are swallowed. Event emission is best-effort and must never disrupt the
/// orchestration session.
/// </para>
/// </summary>
public sealed class EventEmitter : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<IOrchestrationHook> _hooks = [];
    private readonly ILogger<EventEmitter>? _logger;
    private string? _sessionId;
    private int?    _currentTurn;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public EventEmitter(string path, ILogger<EventEmitter>? logger = null)
    {
        _path   = path;
        _logger = logger;
    }

    /// <summary>Stamps every subsequent event with this turn index when <c>turn</c> is not explicitly passed to <see cref="EmitAsync"/>.</summary>
    public void SetTurn(int turn) => _currentTurn = turn;

    /// <summary>Stamps every subsequent event with this session ID.</summary>
    public void SetSessionId(string sessionId)
    {
        // Reject characters that would break JSONL — the session ID is serialised as a
        // JSON string value and the file format assumes one event per line.
        if (sessionId.Any(c => c == '"' || c == '\\' || c < 0x20))
            throw new ArgumentException(
                "Session ID must not contain quotes, backslashes, or control characters.", nameof(sessionId));
        _sessionId = sessionId;
    }

    /// <summary>
    /// Registers a hook to be called after each event is written.
    /// Hooks are invoked in registration order. Returns <c>this</c> for chaining.
    /// </summary>
    public EventEmitter RegisterHook(IOrchestrationHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _hooks.Add(hook);
        return this;
    }

    /// <summary>
    /// Appends one JSONL event and dispatches it to all registered hooks.
    /// Never throws — errors are swallowed to keep the session alive.
    /// </summary>
    public async Task EmitAsync(
        string  eventType,
        string? agent   = null,
        int?    turn    = null,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow;

        var line = JsonSerializer.Serialize(new SessionEvent(
            Ts:        timestamp.ToString("O"),
            Session:   _sessionId,
            Agent:     agent,
            Turn:      turn ?? _currentTurn,
            EventType: eventType,
            Payload:   payload), JsonOpts) + "\n";

        // Serialize file writes so concurrent turns cannot interleave lines.
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(_path, line).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort — event file I/O must not disrupt the session.
            _logger?.LogWarning(ex, "Failed to write event '{EventType}' to {Path}", eventType, _path);
        }
        finally { _lock.Release(); }

        // Dispatch to hooks outside the lock — hooks must not block file I/O for other emitters.
        if (_hooks.Count > 0)
        {
            var evt = new OrchestrationEvent(
                EventType: eventType,
                Timestamp: timestamp,
                SessionId: _sessionId,
                Agent:     agent,
                Turn:      turn ?? _currentTurn,
                Payload:   payload);

            foreach (var hook in _hooks)
            {
                try { await hook.OnEventAsync(evt, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // Best-effort — a misbehaving hook must not kill the session.
                    _logger?.LogWarning(ex,
                        "Hook {Hook} threw on event '{EventType}' (session={Session})",
                        hook.GetType().Name, eventType, _sessionId);
                }
            }
        }
    }

    public void Dispose() => _lock.Dispose();

    private sealed record SessionEvent(
        string  Ts,
        string? Session,
        string? Agent,
        int?    Turn,
        string  EventType,
        object? Payload);
}
