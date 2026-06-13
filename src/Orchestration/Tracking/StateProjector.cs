using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using Microsoft.Extensions.Logging;

namespace fuseraft.Orchestration.Tracking;

/// <summary>
/// Projects invocation records and typed execution events into <see cref="ExecutionState"/>
/// and writes <c>execution-state.json</c> after every turn.
///
/// <para>ChangeTracker calls <see cref="ProjectAsync"/> after each turn's flush.</para>
/// <para>ShellPlugin calls <see cref="IEventSink.Emit"/> during shell_run execution.</para>
/// </summary>
public sealed class StateProjector : IEventSink
{
    private string _sessionId;
    private readonly string _statePath;
    private readonly ILogger<StateProjector>? _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly ConcurrentQueue<ExecutionEvent> _pending = new();

    private const int MaxFailedAttempts     = 10;
    private const int MaxSignificantChanges = 50;
    private const int MaxCompilerErrors     = 20;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // .NET:  "path/to/File.cs(44,13): error CS0246: type or namespace not found"
    private static readonly Regex DotNetError =
        new(@"^(.+?)\((\d+),\d+\): error (CS\d+|FS\d+): (.+)$",
            RegexOptions.Compiled | RegexOptions.Singleline);

    // Rust:  "error[E0308]: mismatched types"
    private static readonly Regex RustError =
        new(@"^error\[(E\d+)\]: (.+?)\s*-->\s*(.+?):(\d+):\d+",
            RegexOptions.Compiled | RegexOptions.Singleline);

    // Go:    "./path/file.go:44:13: undefined: Foo"
    private static readonly Regex GoError =
        new(@"^(\./[^:]+):(\d+):\d+: (.+)$",
            RegexOptions.Compiled | RegexOptions.Singleline);

    public StateProjector(string statePath, string sessionId, ILogger<StateProjector>? logger = null)
    {
        _statePath = statePath;
        _sessionId = sessionId;
        _logger    = logger;
    }

    void IEventSink.Emit(ExecutionEvent evt) => _pending.Enqueue(evt);

    internal void SetSessionId(string id) => _sessionId = id;

    /// <summary>
    /// Called once at session start. If the on-disk state belongs to a different session,
    /// overwrites it with a clean state so prior-run build status, failed attempts, and
    /// file-change records never bleed into a brand-new session.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_statePath)) return;

            var raw   = await File.ReadAllTextAsync(_statePath, ct);
            var state = JsonSerializer.Deserialize<ExecutionState>(raw, JsonOpts);

            if (state is null
                || string.IsNullOrEmpty(state.SessionId)
                || state.SessionId == _sessionId)
                return;

            await WriteCoreAsync(new ExecutionState { SessionId = _sessionId }, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "StateProjector: failed to initialize '{Path}'.", _statePath);
        }
        finally { _fileLock.Release(); }
    }

    /// <summary>
    /// Called by ChangeTracker after each turn's invocations are flushed.
    /// Drains the typed event queue and processes invocation records, then writes
    /// execution-state.json.
    /// </summary>
    public async Task ProjectAsync(
        IReadOnlyList<InvocationRecord> invocations,
        string agent,
        int turn,
        CancellationToken ct)
    {
        var typedEvents = new List<ExecutionEvent>();
        while (_pending.TryDequeue(out var evt)) typedEvents.Add(evt);

        if (invocations.Count == 0 && typedEvents.Count == 0)
            return;

        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ExecutionState state;
            try
            {
                state = await ReadCoreAsync(ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "StateProjector: failed to read '{Path}' — state reset.", _statePath);
                state = new ExecutionState { SessionId = _sessionId };
            }

            foreach (var evt in typedEvents)
                state = ApplyEvent(state, evt);

            foreach (var inv in invocations)
                state = ApplyInvocation(state, inv);

            try
            {
                await WriteCoreAsync(state with { LastUpdated = DateTimeOffset.UtcNow, SessionId = _sessionId }, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "StateProjector: failed to write '{Path}'.", _statePath);
            }
        }
        finally { _fileLock.Release(); }
    }

    private static ExecutionState ApplyEvent(ExecutionState state, ExecutionEvent evt) =>
        evt switch
        {
            BuildResultEvent b      => ApplyBuildResult(state, b),
            AttemptFailedEvent f    => ApplyAttemptFailed(state, f),
            AttemptSucceededEvent   => state,
            TaskOpenedEvent t       => ApplyTaskOpened(state, t),
            TaskCompletedEvent c    => ApplyTaskCompleted(state, c),
            _                       => state,
        };

    private static ExecutionState ApplyBuildResult(ExecutionState state, BuildResultEvent evt)
    {
        var newBuild = new BuildState
        {
            Succeeded      = evt.Succeeded,
            ExitCode       = evt.ExitCode,
            Command        = evt.Command,
            Errors         = evt.Errors,
            LastGoodCommit = evt.Succeeded ? evt.CommitHash : state.Build.LastGoodCommit,
            Timestamp      = evt.Timestamp,
        };

        List<ValidationFailure> newFailures;
        if (evt.Succeeded)
        {
            newFailures = [];
        }
        else
        {
            newFailures = [.. state.ActiveFailures,
                           .. evt.Errors
                              .Take(MaxCompilerErrors)
                              .Select(ParseValidationFailure)
                              .OfType<ValidationFailure>()];
        }

        return state with { Build = newBuild, ActiveFailures = newFailures };
    }

    private static ExecutionState ApplyAttemptFailed(ExecutionState state, AttemptFailedEvent evt)
    {
        var record = new AttemptRecord
        {
            Description  = evt.Description,
            Outcome      = "failed",
            ErrorSummary = evt.ErrorSummary,
            Timestamp    = evt.Timestamp,
        };
        var updated = new List<AttemptRecord>(state.FailedAttempts) { record };
        if (updated.Count > MaxFailedAttempts)
            updated = updated[^MaxFailedAttempts..];
        return state with { FailedAttempts = updated };
    }

    private static ExecutionState ApplyTaskOpened(ExecutionState state, TaskOpenedEvent evt)
    {
        var task = new OpenTask { Description = evt.Description, Status = "pending" };
        return state with { OpenTasks = [.. state.OpenTasks, task] };
    }

    private static ExecutionState ApplyTaskCompleted(ExecutionState state, TaskCompletedEvent evt)
    {
        var updated = state.OpenTasks
            .Where(t => !t.Description.Equals(evt.Description, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return state with { OpenTasks = updated };
    }

    private static ExecutionState ApplyInvocation(ExecutionState state, InvocationRecord inv)
    {
        if (!inv.Succeeded) return state;

        string? operation = null;
        string? path      = null;

        if (FunctionNameMatches(inv.Name, "write_file"))
        {
            operation = "written";
            path      = OrchestratorHelpers.GetArg(inv.Args, "path");
        }
        else if (FunctionNameMatches(inv.Name, "patch_file"))
        {
            operation = "patched";
            path      = OrchestratorHelpers.GetArg(inv.Args, "path");
        }
        else if (FunctionNameMatches(inv.Name, "copy_file") || FunctionNameMatches(inv.Name, "move_file"))
        {
            operation = "written";
            path      = OrchestratorHelpers.GetArg(inv.Args, "destination");
        }
        else if (FunctionNameMatches(inv.Name, "delete_file") || FunctionNameMatches(inv.Name, "delete_directory"))
        {
            operation = "deleted";
            path      = OrchestratorHelpers.GetArg(inv.Args, "path");
        }

        if (operation is null || string.IsNullOrWhiteSpace(path))
            return state;

        var record  = new FileChangeRecord { Path = path, Operation = operation, Timestamp = DateTimeOffset.UtcNow };
        var updated = new List<FileChangeRecord>(state.SignificantChanges) { record };
        if (updated.Count > MaxSignificantChanges)
            updated = updated[^MaxSignificantChanges..];
        return state with { SignificantChanges = updated };
    }

    private static ValidationFailure? ParseValidationFailure(string errorLine)
    {
        if (string.IsNullOrWhiteSpace(errorLine)) return null;

        var m = DotNetError.Match(errorLine);
        if (m.Success)
            return new ValidationFailure
            {
                Code    = m.Groups[3].Value,
                File    = m.Groups[1].Value.Trim(),
                Line    = int.TryParse(m.Groups[2].Value, out var l1) ? l1 : 0,
                Message = m.Groups[4].Value.Trim(),
            };

        m = RustError.Match(errorLine);
        if (m.Success)
            return new ValidationFailure
            {
                Code    = m.Groups[1].Value,
                File    = m.Groups[3].Value.Trim(),
                Line    = int.TryParse(m.Groups[4].Value, out var l2) ? l2 : 0,
                Message = m.Groups[2].Value.Trim(),
            };

        m = GoError.Match(errorLine);
        if (m.Success)
            return new ValidationFailure
            {
                Code    = string.Empty,
                File    = m.Groups[1].Value.Trim(),
                Line    = int.TryParse(m.Groups[2].Value, out var l3) ? l3 : 0,
                Message = m.Groups[3].Value.Trim(),
            };

        return new ValidationFailure { Message = errorLine.Trim() };
    }

    private static bool FunctionNameMatches(string name, string pattern) =>
        name.Replace("_", "").Contains(
            pattern.Replace("_", ""),
            StringComparison.OrdinalIgnoreCase);

    // Caller must hold _fileLock.
    private async Task<ExecutionState> ReadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_statePath))
            return new ExecutionState { SessionId = _sessionId };

        var raw   = await File.ReadAllTextAsync(_statePath, ct);
        var state = JsonSerializer.Deserialize<ExecutionState>(raw, JsonOpts)
                 ?? new ExecutionState { SessionId = _sessionId };

        // Different session on disk → start fresh so prior-run build status, failed
        // attempts, and file-change records never bleed into a brand-new session.
        if (!string.IsNullOrEmpty(state.SessionId) && state.SessionId != _sessionId)
            return new ExecutionState { SessionId = _sessionId };

        return state;
    }

    // Caller must hold _fileLock.
    private async Task WriteCoreAsync(ExecutionState state, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_statePath));
        if (dir is not null) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_statePath, JsonSerializer.Serialize(state, JsonOpts), ct);
    }
}
