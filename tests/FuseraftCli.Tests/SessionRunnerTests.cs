using fuseraft.Cli;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using Moq;
using System.Runtime.CompilerServices;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="SessionRunner"/> error-handling paths that do not require live LLM calls.
/// </summary>
public sealed class SessionRunnerTests : IDisposable
{
    private readonly Mock<ISessionStore>         _store    = new();
    private readonly Mock<IHumanApprovalService> _approval = new();

    // Snapshot dump files that existed before this test class was instantiated so
    // Dispose() can remove only the dumps produced during this test run.
    private readonly HashSet<string> _dumpsBefore;

    public SessionRunnerTests()
    {
        _store.Setup(s => s.SaveAsync(It.IsAny<SessionCheckpoint>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        _dumpsBefore = Directory.Exists(FuseraftPaths.GlobalCrashDumps)
            ? [.. Directory.GetFiles(FuseraftPaths.GlobalCrashDumps, "*.json")]
            : [];
    }

    public void Dispose()
    {
        if (!Directory.Exists(FuseraftPaths.GlobalCrashDumps)) return;
        foreach (var f in Directory.GetFiles(FuseraftPaths.GlobalCrashDumps, "*.json"))
            if (!_dumpsBefore.Contains(f))
                try { File.Delete(f); } catch { }
    }

    private SessionRunner MakeRunner(IOrchestrator orchestrator) => new(
        orchestrator,
        compactor:      null,
        _store.Object,
        _approval.Object,
        eventEmitter:   null,
        telemetry:      null,
        modelIdByAgent: new Dictionary<string, string>());

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

        var newDumps = Directory.Exists(FuseraftPaths.GlobalCrashDumps)
            ? Directory.GetFiles(FuseraftPaths.GlobalCrashDumps, "*.json")
                       .Where(f => !_dumpsBefore.Contains(f))
                       .ToList()
            : [];
        Assert.NotEmpty(newDumps);
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
}
