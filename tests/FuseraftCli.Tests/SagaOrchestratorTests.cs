using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Saga;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="SagaOrchestrator"/>.
/// </summary>
public sealed class SagaOrchestratorTests
{
    // -----------------------------------------------------------------------
    // Stack unwind ordering
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CompensationRunsInReverseOrder()
    {
        var order = new List<string>();

        var compensators = new Dictionary<string, ICompensatingAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent1"] = new RecordingCompensator("Agent1", order),
            ["Agent2"] = new RecordingCompensator("Agent2", order),
        };

        var inner = new FailingAfterNOrchestrator(
            [
                AgentMsg("Agent1"),
                AgentMsg("Agent2"),
            ],
            failAfterAll: true);

        var saga = new SagaOrchestrator(inner, new SagaConfig { Enabled = true }, compensators);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in saga.StreamAsync("task")) { }
        });

        // Compensation should run in reverse: Agent2 first, then Agent1.
        Assert.Equal(["Agent2", "Agent1"], order);
    }

    [Fact]
    public async Task StepsWithoutCompensatorAreSkipped()
    {
        var order = new List<string>();

        // Only Agent1 has a compensator — Agent2 does not.
        var compensators = new Dictionary<string, ICompensatingAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent1"] = new RecordingCompensator("Agent1", order),
        };

        var inner = new FailingAfterNOrchestrator(
            [
                AgentMsg("Agent1"),
                AgentMsg("Agent2"),
            ],
            failAfterAll: true);

        var saga = new SagaOrchestrator(inner, new SagaConfig { Enabled = true }, compensators);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in saga.StreamAsync("task")) { }
        });

        // Only Agent1's compensator ran.
        Assert.Equal(["Agent1"], order);
    }

    // -----------------------------------------------------------------------
    // MaxCompensationSteps safety limit
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MaxCompensationStepsLimitsUnwind()
    {
        var order = new List<string>();

        var compensators = new Dictionary<string, ICompensatingAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent1"] = new RecordingCompensator("Agent1", order),
            ["Agent2"] = new RecordingCompensator("Agent2", order),
            ["Agent3"] = new RecordingCompensator("Agent3", order),
        };

        var inner = new FailingAfterNOrchestrator(
            [AgentMsg("Agent1"), AgentMsg("Agent2"), AgentMsg("Agent3")],
            failAfterAll: true);

        // Limit compensation to 2 steps out of 3 eligible.
        var saga = new SagaOrchestrator(inner, new SagaConfig { Enabled = true, MaxCompensationSteps = 2 }, compensators);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in saga.StreamAsync("task")) { }
        });

        Assert.Equal(2, order.Count);
    }

    // -----------------------------------------------------------------------
    // Happy path — no compensation on success
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SuccessfulRun_NoCompensationCalled()
    {
        var order = new List<string>();

        var compensators = new Dictionary<string, ICompensatingAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent1"] = new RecordingCompensator("Agent1", order),
        };

        var inner = new FailingAfterNOrchestrator([AgentMsg("Agent1")], failAfterAll: false);

        var saga = new SagaOrchestrator(inner, new SagaConfig { Enabled = true }, compensators);

        await foreach (var _ in saga.StreamAsync("task")) { }

        Assert.Empty(order);
    }

    // -----------------------------------------------------------------------
    // IOrchestrator delegation
    // -----------------------------------------------------------------------

    [Fact]
    public void SetSessionId_DelegatesToInner()
    {
        var inner = new TrackingOrchestrator();
        var saga  = new SagaOrchestrator(inner, new SagaConfig { Enabled = true });

        saga.SetSessionId("abc123");

        Assert.Equal("abc123", inner.LastSessionId);
    }

    [Fact]
    public void SetResumeExecutorId_DelegatesToInner()
    {
        var inner = new TrackingOrchestrator();
        var saga  = new SagaOrchestrator(inner, new SagaConfig { Enabled = true });

        saga.SetResumeExecutorId("developer");

        Assert.Equal("developer", inner.LastResumeExecutorId);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AgentMessage AgentMsg(string agentName) => new()
    {
        AgentName = agentName,
        Content   = $"Output from {agentName}",
        Role      = "assistant",
        TurnIndex = 0,
    };

    private sealed class RecordingCompensator(string name, List<string> order) : ICompensatingAgent
    {
        public Task<AgentState> CompensateAsync(AgentState state, CancellationToken ct)
        {
            order.Add(name);
            return Task.FromResult(state);
        }
    }

    /// <summary>
    /// Streams a fixed list of messages and then optionally throws.
    /// </summary>
    private sealed class FailingAfterNOrchestrator(
        IReadOnlyList<AgentMessage> messages,
        bool failAfterAll) : fuseraft.Core.Interfaces.IOrchestrator
    {
        public event Action<string>? AgentStarting { add { } remove { } }
        public event Action<string, string, string?>? ToolCalling { add { } remove { } }
        public event Action<string, int, int>? TokenBudgetWarning { add { } remove { } }
        public void SetSessionId(string sessionId) { }
        public void SetResumeExecutorId(string? executorId) { }

        public Task<OrchestrationResult> RunAsync(
            string task,
            IReadOnlyList<AgentMessage>? priorHistory = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OrchestrationResult
            {
                SessionId = string.Empty, Succeeded = true, Messages = [],
                Duration = TimeSpan.Zero, TerminationReason = "Test"
            });

        public async IAsyncEnumerable<AgentMessage> StreamAsync(
            string task,
            IReadOnlyList<AgentMessage>? priorHistory = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var msg in messages)
            {
                yield return msg;
                await Task.Yield();
            }

            if (failAfterAll)
                throw new InvalidOperationException("Simulated agent failure.");
        }
    }

    private sealed class TrackingOrchestrator : fuseraft.Core.Interfaces.IOrchestrator
    {
        public string? LastSessionId        { get; private set; }
        public string? LastResumeExecutorId { get; private set; }
        public event Action<string>? AgentStarting { add { } remove { } }
        public event Action<string, string, string?>? ToolCalling { add { } remove { } }
        public event Action<string, int, int>? TokenBudgetWarning { add { } remove { } }
        public void SetSessionId(string sessionId) => LastSessionId = sessionId;
        public void SetResumeExecutorId(string? executorId) => LastResumeExecutorId = executorId;

        public Task<OrchestrationResult> RunAsync(
            string task,
            IReadOnlyList<AgentMessage>? priorHistory = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new OrchestrationResult
            {
                SessionId = string.Empty, Succeeded = true, Messages = [],
                Duration = TimeSpan.Zero, TerminationReason = "Test"
            });

        public async IAsyncEnumerable<AgentMessage> StreamAsync(
            string task,
            IReadOnlyList<AgentMessage>? priorHistory = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
