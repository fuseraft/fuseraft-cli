using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Chat;
using fuseraft.Infrastructure.KeyStore;

namespace FuseraftCli.Tests;

/// <summary>
/// A goal paused on a question continues with the user's next plain message — audited like any
/// other turn — and can pause again when that answer doesn't settle the question. Driven through
/// <see cref="ReplCommands.ContinuePausedGoalAsync"/> with a fake model that plays both the agent
/// (streaming) and the judge (single response).
/// </summary>
[Collection("FuseraftHomeEnv")]
public sealed class ReplGoalContinuationTests : IDisposable
{
    private readonly string? _originalHome = Environment.GetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar);
    private readonly string _tempHome = Path.Combine(Path.GetTempPath(), $"fuseraft-test-{Guid.NewGuid():N}");
    private readonly List<string> _eventsPaths = [];
    private readonly List<ReplSessionContext> _contexts = [];

    public ReplGoalContinuationTests() =>
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _tempHome);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FuseraftPaths.HomeOverrideEnvVar, _originalHome);
        if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true);

        foreach (var ctx in _contexts)
        {
            ctx.Emitter.Dispose();
            ctx.Factory.Dispose();
        }
        foreach (var path in _eventsPaths)
            if (File.Exists(path)) File.Delete(path);
    }

    private const string Objective = "set up the database layer";

    private static GoalRecord Paused(int max = 5) =>
        new(Objective, GoalEnd.Blocked, 1, new GoalVerdict(0.2, false, true, "which database?"), max);

    private static string Verdict(bool complete = false, bool blocked = false, string missing = "", double score = 0.5) =>
        System.Text.Json.JsonSerializer.Serialize(new { score, complete, blocked, missing });

    /// <summary>Agent turns replay <paramref name="agentReplies"/> in order; judge calls replay <paramref name="verdicts"/>.</summary>
    private sealed class ScriptedClient(IEnumerable<string> agentReplies, IEnumerable<string> verdicts) : IChatClient
    {
        private readonly Queue<string> _agent = new(agentReplies);
        private readonly Queue<string> _judge = new(verdicts);

        public int AgentTurns { get; private set; }
        public List<string> JudgePrompts { get; } = [];

        public ChatClientMetadata Metadata => new("test", null!, "stub");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            JudgePrompts.Add(messages.Last().Text);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _judge.Dequeue())));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AgentTurns++;
            await Task.Yield();
            yield return new ChatResponseUpdate
            {
                Role     = ChatRole.Assistant,
                Contents = [new TextContent(_agent.Dequeue()), new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
            };
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    private ReplSessionContext NewContext(IChatClient client)
    {
        var eventsPath = Path.Combine(Path.GetTempPath(), $"fuseraft-test-events-{Guid.NewGuid():N}.jsonl");
        _eventsPaths.Add(eventsPath);
        var ctx = new ReplSessionContext(
            cwd: "/tmp", sessionId: "goal-continuation-session", startedAt: DateTime.UtcNow,
            modelId: "test-model", modelConfig: new() { ModelId = "test-model" },
            userCfg: null, client: client, factory: new ChatClientFactory(),
            keyStore: new UnavailableKeyStore(),
            emitter: new EventEmitter(eventsPath),
            eventsPath: eventsPath,
            memoryStore: MemoryStore.CreateForTest(Path.Combine(Path.GetTempPath(), $"fuseraft-test-mem-{Guid.NewGuid():N}")),
            toolsByCategory: [], systemPrompt: "test system prompt", pendingSave: false,
            adaptiveTrimTracker: new());
        ctx.JsonMode = true;
        _contexts.Add(ctx);
        return ctx;
    }

    [Fact]
    public void AwaitsReply_IsTrueOnlyForABlockedGoal()
    {
        foreach (var end in Enum.GetValues<GoalEnd>())
            Assert.Equal(end == GoalEnd.Blocked, new GoalRecord("x", end, 1, null).AwaitsReply);
    }

    [Fact]
    public async Task Reply_ContinuesTheGoal_AndFinishesWhenTheAnswerSettlesIt()
    {
        var client = new ScriptedClient(["Acknowledged, continuing with postgres."], [Verdict(complete: true, score: 0.95)]);
        var ctx = NewContext(client);
        ctx.LastGoal = Paused();

        await ReplCommands.ContinuePausedGoalAsync(ctx, ctx.LastGoal, "use postgres", [], CancellationToken.None);

        Assert.Equal(1, client.AgentTurns);
        var user = Assert.Single(ctx.History, m => m.Role == ChatRole.User);
        Assert.Equal("use postgres", user.Text);                       // the reply goes in verbatim, not wrapped
        var judged = Assert.Single(client.JudgePrompts);
        Assert.Contains(Objective, judged);                            // audited against the original objective
        Assert.Contains("use postgres", judged);                       // and it can see the answer
        Assert.Equal(GoalEnd.Complete, ctx.LastGoal!.End);
        Assert.False(ctx.LastGoal.AwaitsReply);
    }

    [Fact]
    public async Task Reply_ThatDoesNotSettleIt_LetsTheAgentPushBack_AndPausesAgain()
    {
        var client = new ScriptedClient(
            ["That doesn't tell me which database to use - postgres or sqlite?"],
            [Verdict(blocked: true, missing: "user has not said which database", score: 0.1)]);
        var ctx = NewContext(client);
        ctx.LastGoal = Paused();

        await ReplCommands.ContinuePausedGoalAsync(ctx, ctx.LastGoal, "whatever", [], CancellationToken.None);

        Assert.Equal(1, client.AgentTurns);                            // no nudge to guess after the pushback
        Assert.DoesNotContain(ctx.History, m => m.Text.StartsWith(ReplGoal.FollowUpPrefix, StringComparison.Ordinal));
        Assert.Equal(GoalEnd.Blocked, ctx.LastGoal!.End);
        Assert.True(ctx.LastGoal.AwaitsReply);                         // the next reply continues it again
    }

    [Fact]
    public async Task Continuation_KeepsTheOriginalBudget()
    {
        var client = new ScriptedClient(
            ["Working.", "Still working."],
            [Verdict(missing: "migrations not written"), Verdict(missing: "seed data not loaded")]);
        var ctx = NewContext(client);
        ctx.LastGoal = Paused(max: 2);

        await ReplCommands.ContinuePausedGoalAsync(ctx, ctx.LastGoal, "postgres", [], CancellationToken.None);

        Assert.Equal(2, client.AgentTurns);
        Assert.Equal(GoalEnd.Capped, ctx.LastGoal!.End);
        Assert.Equal(2, ctx.LastGoal.Audits);
        Assert.Equal(2, ctx.LastGoal.MaxIterations);
    }

    [Fact]
    public async Task Drop_ForgetsTheGoal_SoALaterMessageIsAnOrdinaryTurn()
    {
        var ctx = NewContext(new ScriptedClient([], []));
        ctx.LastGoal = Paused();

        await ReplCommands.HandleAsync(ctx, "/goal", "drop", CancellationToken.None);

        Assert.Null(ctx.LastGoal);
        await ReplCommands.HandleAsync(ctx, "/goal", "drop", CancellationToken.None);   // nothing left: harmless
        Assert.Null(ctx.LastGoal);
    }

    [Fact]
    public void JudgePrompt_TreatsAnUnsettledFollowUpQuestionAsBlocked()
    {
        Assert.Contains("earlier answer did not settle", ReplGoal.BuildJudgePrompt("x", "y"));
    }
}
