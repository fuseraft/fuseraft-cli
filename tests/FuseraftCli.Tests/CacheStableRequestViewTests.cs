using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

public sealed class CacheStableRequestViewTests
{
    private const int MaxChars = 200_000;

    private static CacheStableRequestView NewView() => new((stored, target, ct) => AgentContextCompactionFilters.TrimNearBudget(
        AgentContextCompactionFilters.DropSupersededContent(stored), maxInTurnToolPairs: 12, target, triggerChars: 0, ct));

    // One agentic turn: a request, then rounds of a tool call and its result, each result a different file.
    // Each call comes with a few lines of the model's reasoning, as real ones do.
    private static List<ChatMessage> Round(int i, int resultChars) =>
    [
        new(ChatRole.Assistant, [
            new TextContent($"Round {i}: the failing path goes through File{i}, so I'll read it next. " + new string('r', 400)),
            new FunctionCallContent($"c{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"src/File{i}.cs" })]),
        new(ChatRole.Tool, [new FunctionResultContent($"c{i}", $"file {i}\n" + new string('x', resultChars))]),
    ];

    // How many requests changed a message an earlier request had sent: each is a prompt-cache miss.
    private static async Task<(int Misses, int Requests, List<IReadOnlyList<ChatMessage>> Sent)> ReplayAsync(
        Func<IReadOnlyList<ChatMessage>, Task<IReadOnlyList<ChatMessage>>> render, int rounds, int resultChars,
        ChatMessage? request = null)
    {
        var history = new List<ChatMessage> { new(ChatRole.System, "system"), request ?? new(ChatRole.User, "fix the bug") };
        var tracker = new RequestPrefixTracker();
        var sentAll = new List<IReadOnlyList<ChatMessage>>();
        var misses = 0;
        for (var i = 0; i < rounds; i++)
        {
            var sent = await render(history);
            sentAll.Add(sent);
            if (tracker.Observe(sent).ChangedAt is not null) misses++;
            history.AddRange(Round(i, resultChars));
        }
        return (misses, rounds, sentAll);
    }

    [Fact]
    public async Task UnderBudget_SendsTheHistoryAsIs_AndEveryRequestExtendsTheLast()
    {
        var view = NewView();
        var (misses, _, sent) = await ReplayAsync(h => view.RenderAsync(null, h, MaxChars, default), rounds: 10, resultChars: 2_000);

        Assert.Equal(0, misses);
        Assert.Equal(2 + 2 * 9, sent[^1].Count);
    }

    [Fact]
    public async Task LongTurnPastTheBudget_MissesTheCacheFarLessThanRetrimmingEveryRequest()
    {
        var view = NewView();
        var (stable, requests, sent) = await ReplayAsync(h => view.RenderAsync(null, h, MaxChars, default), rounds: 120, resultChars: 6_000);
        var (retrim, _, _) = await ReplayAsync(async h => (await AgentContextCompactionFilters.ApplyInTurnFilters(
            h, maxInTurnToolPairs: 12, MaxChars, triggerChars: MaxChars, cacheStep: AgentContextCompactionFilters.CacheStableTrimStep)).ToList(),
            rounds: 120, resultChars: 6_000);

        // ~720K chars of results against a 200K budget: each trim frees an 80K band, about 13 rounds of growth.
        Assert.True(stable <= 10, $"{stable} of {requests} requests missed");
        Assert.True(stable * 3 <= retrim, $"append-only missed {stable}, re-trimming every request missed {retrim}");
        Assert.All(sent, s => Assert.True(AgentContextCompactionFilters.EstimateTotalChars(s) <= MaxChars + 6_100));
    }

    [Fact]
    public async Task ATrim_CutsToTheLowWaterMark_AndKeepsTheNewestResult()
    {
        var view = NewView();
        var history = new List<ChatMessage> { new(ChatRole.User, "go") };
        for (var i = 0; i < 40; i++) history.AddRange(Round(i, 6_000));

        var sent = await view.RenderAsync(null, history, MaxChars, default);

        Assert.True(AgentContextCompactionFilters.EstimateTotalChars(sent) <= MaxChars * CacheStableRequestView.DefaultLowWaterRatio);
        Assert.Contains("file 39", sent[^1].Contents.OfType<FunctionResultContent>().Single().Result!.ToString());
    }

    [Fact]
    public async Task AMessageBiggerThanTheBudget_IsNotRetrimmedEveryRound()
    {
        // A paste over the budget can't be trimmed away: retrying the trim each round would miss the cache every time.
        var view = NewView();
        var paste = new ChatMessage(ChatRole.User, "build log\n" + new string('l', 260_000));
        var (misses, requests, _) = await ReplayAsync(h => view.RenderAsync(null, h, MaxChars, default), rounds: 20, resultChars: 2_000, request: paste);

        Assert.True(misses <= 1, $"{misses} of {requests} requests missed");
    }

    [Fact]
    public async Task RewrittenHistory_StartsAFreshView()
    {
        var view = NewView();
        var history = new List<ChatMessage> { new(ChatRole.User, "first") };
        history.AddRange(Round(0, 100));
        await view.RenderAsync(null, history, MaxChars, default);

        var compacted = new List<ChatMessage> { new(ChatRole.User, "summary"), new(ChatRole.User, "next") };
        var sent = await view.RenderAsync(null, compacted, MaxChars, default);

        Assert.Equal(["summary", "next"], sent.Select(m => m.Text));
    }

    [Fact]
    public async Task EachRun_KeepsItsOwnView()
    {
        var view = NewView();
        var main = new List<ChatMessage> { new(ChatRole.User, "main task") };
        var sub  = new List<ChatMessage> { new(ChatRole.User, "subagent task") };

        await view.RenderAsync(null, main, MaxChars, default);
        await view.RenderAsync("run-1", sub, MaxChars, default);
        main.Add(new ChatMessage(ChatRole.Assistant, "done"));

        Assert.Equal(["main task", "done"], (await view.RenderAsync(null, main, MaxChars, default)).Select(m => m.Text));
    }
}
