using System.Runtime.CompilerServices;
using System.Text.Json;
using fuseraft.Cli;
using fuseraft.Core;
using fuseraft.Core.Exceptions;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Orchestration;
using Moq;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="SessionRunner"/> error-handling paths that do not require live LLM calls.
///
/// <para>
/// Isolates <c>FUSERAFT_HOME</c> to a throwaway temp dir for the crash-dump test below. Without
/// this, the test read/wrote the real user's <c>~/.fuseraft/crashdump</c>, and — because
/// <see cref="FuseraftPaths.GlobalCrashDumps"/> re-reads the env var on every access rather than
/// caching it — a concurrently-running test in a different xUnit collection that also mutates
/// <c>FUSERAFT_HOME</c> (e.g. <see cref="ReplForkTodoPersistenceTests"/>) could flip the resolved
/// path between this test's "before" snapshot, the actual dump write, and its "after" assertion,
/// so the dump landed somewhere the assertion never looked. That produced an intermittent
/// "Assert.NotEmpty() Failure: Collection was empty" with no relation to the code under test.
/// </para>
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class SessionRunnerTests : IDisposable
{
    private readonly Mock<ISessionStore>         _store    = new();
    private readonly Mock<IHumanApprovalService> _approval = new();

    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string  _tempHome     = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");

    public SessionRunnerTests()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

        _store.Setup(s => s.SaveAsync(It.IsAny<SessionCheckpoint>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);
    }

    private SessionRunner MakeRunner(IOrchestrator orchestrator) => new(
        orchestrator,
        compactor:      null,
        _store.Object,
        _approval.Object,
        eventEmitter:   null,
        telemetry:      null,
        modelIdByAgent: new Dictionary<string, string>());

    private SessionRunner MakeRunnerWithEmitter(
        IOrchestrator orchestrator,
        EventEmitter emitter,
        int maxIterations = 0) => new(
        orchestrator,
        compactor:      null,
        _store.Object,
        _approval.Object,
        eventEmitter:   emitter,
        telemetry:      null,
        modelIdByAgent: new Dictionary<string, string>(),
        maxIterations:  maxIterations,
        quiet:          true);

    private static SessionCheckpoint MakeCheckpoint() => new()
    {
        SessionId  = Guid.NewGuid().ToString("N")[..8],
        Task       = "test task",
        ConfigPath = string.Empty,
    };

    // -----------------------------------------------------------------------
    // Unexpected exception in StreamAsync → crash dump written
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_UnexpectedException_WritesCrashDump()
    {
        var runner = MakeRunner(new ThrowingOrchestrator(new InvalidOperationException("plugin exploded")));

        await runner.RunAsync("task", MakeCheckpoint(), hitlMode: false, showTools: false, CancellationToken.None);

        // _tempHome is a fresh directory this test instance owns exclusively, so any dump
        // found here is unambiguously the one this run wrote — no before/after diffing needed.
        Assert.True(Directory.Exists(FuseraftPaths.GlobalCrashDumps));
        Assert.NotEmpty(Directory.GetFiles(FuseraftPaths.GlobalCrashDumps, "*.json"));
    }

    [Fact]
    public async Task RunAsync_UnexpectedException_ReturnsFailureWithMessage()
    {
        const string message = "something broke";
        var runner = MakeRunner(new ThrowingOrchestrator(new InvalidOperationException(message)));

        var result = await runner.RunAsync("task", MakeCheckpoint(), hitlMode: false, showTools: false, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(message, result.ErrorMessage);
    }

    // -----------------------------------------------------------------------
    // Event wiring: emitted EventTypes constants
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_OperationCancelled_EmitsCancellationRequested()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            using var emitter = new EventEmitter(tmp);
            var tcs = new TaskCompletionSource();
            emitter.RegisterHook(new SignalOnEventHook(EventTypes.CancellationRequested, tcs));

            var runner = MakeRunnerWithEmitter(
                new ThrowingOrchestrator(new OperationCanceledException()), emitter);

            await runner.RunAsync("task", MakeCheckpoint(), hitlMode: false, showTools: false, CancellationToken.None);

            // CancellationRequested is fire-and-forget; wait for the hook to signal completion.
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public async Task RunAsync_MaxIterationsHit_EmitsMaxTurnsExceeded()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            using var emitter = new EventEmitter(tmp);
            var runner = MakeRunnerWithEmitter(new EmptyOrchestrator(), emitter, maxIterations: 1);

            var checkpoint = MakeCheckpoint();
            checkpoint.Messages.Add(new AgentMessage
            {
                AgentName = "Agent",
                Content   = "done",
                Role      = "assistant",
                TurnIndex = 0,
            });

            await runner.RunAsync("task", checkpoint, hitlMode: false, showTools: false, CancellationToken.None);

            var events = await ReadEventTypesAsync(tmp);
            Assert.Contains(EventTypes.MaxTurnsExceeded, events);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public async Task RunAsync_AgentBlocked_WithRedirect_EmitsHitlResolved()
    {
        _approval
            .SetupSequence(a => a.PromptBlockerResolutionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("proceed")
            .ReturnsAsync((string?)null);

        var tmp = Path.GetTempFileName();
        try
        {
            using var emitter = new EventEmitter(tmp);
            var runner = MakeRunnerWithEmitter(
                new ThrowingOrchestrator(new AgentBlockedException("TestAgent", "stuck")), emitter);

            await runner.RunAsync("task", MakeCheckpoint(), hitlMode: false, showTools: false, CancellationToken.None);

            var events = await ReadEventTypesAsync(tmp);
            Assert.Contains(EventTypes.HitlResolved, events);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public async Task RunAsync_ValidatorStuck_WithRedirect_EmitsHitlResolved()
    {
        _approval
            .SetupSequence(a => a.PromptValidatorStuckAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync("try again")
            .ReturnsAsync((string?)null);

        var tmp = Path.GetTempFileName();
        try
        {
            using var emitter = new EventEmitter(tmp);
            var runner = MakeRunnerWithEmitter(
                new ThrowingOrchestrator(new ValidatorStuckException("TestAgent", "RequireBrief", 3, "no brief")), emitter);

            await runner.RunAsync("task", MakeCheckpoint(), hitlMode: false, showTools: false, CancellationToken.None);

            var events = await ReadEventTypesAsync(tmp);
            Assert.Contains(EventTypes.HitlResolved, events);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private static async Task<List<string>> ReadEventTypesAsync(string path)
    {
        if (!File.Exists(path)) return [];
        var result = new List<string>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("event_type", out var et))
                result.Add(et.GetString() ?? "");
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // Stub orchestrator that throws during StreamAsync
    // -----------------------------------------------------------------------

    private sealed class ThrowingOrchestrator(Exception ex) : IOrchestrator
    {
        // No-op event implementations: SessionRunner subscribes/unsubscribes these
        // during the spinner iteration; the throw happens before any events fire.
        public event Action<string>?                  AgentStarting    { add { } remove { } }
        public event Action<string, string, string?>? ToolCalling      { add { } remove { } }
        public event Action<string, int, int>?        TokenBudgetWarning { add { } remove { } }

        public Task<OrchestrationResult> RunAsync(
            string task,
            IReadOnlyList<AgentMessage>? priorHistory = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<OrchestrationResult>(ex);

        public async IAsyncEnumerable<AgentMessage> StreamAsync(
            string task,
            IReadOnlyList<AgentMessage>? priorHistory = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
#pragma warning disable CS0162 // yield break is unreachable but required to make this an iterator
            throw ex;
            yield break;
#pragma warning restore CS0162
        }

        public void SetSessionId(string sessionId) { }
    }

    // Orchestrator that completes immediately without yielding any messages.
    private sealed class EmptyOrchestrator : IOrchestrator
    {
        public event Action<string>?                  AgentStarting    { add { } remove { } }
        public event Action<string, string, string?>? ToolCalling      { add { } remove { } }
        public event Action<string, int, int>?        TokenBudgetWarning { add { } remove { } }

        public Task<OrchestrationResult> RunAsync(
            string task,
            IReadOnlyList<AgentMessage>? priorHistory = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OrchestrationResult { SessionId = "test", Succeeded = true });

        public async IAsyncEnumerable<AgentMessage> StreamAsync(
            string task,
            IReadOnlyList<AgentMessage>? priorHistory = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void SetSessionId(string sessionId) { }
    }

    // Signals a TaskCompletionSource when a specific event type is observed via a hook.
    private sealed class SignalOnEventHook(string watchFor, TaskCompletionSource tcs) : IOrchestrationHook
    {
        public Task OnEventAsync(OrchestrationEvent evt, CancellationToken cancellationToken = default)
        {
            if (evt.EventType == watchFor) tcs.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
