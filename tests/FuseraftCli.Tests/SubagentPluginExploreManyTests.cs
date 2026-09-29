using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Core;
using fuseraft.Core.Events;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="SubagentPlugin.ExploreManyAsync"/> (<c>subagent_explore_many</c>): explorations run
/// concurrently, stay strictly read-only, never show an approval prompt, and tag their events with
/// a per-run id so a consumer can tell them apart.
/// </summary>
public sealed class SubagentPluginExploreManyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-explore-many-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task RunsTheExplorationsAtTheSameTime_AndReturnsEveryReportInOrder()
    {
        var plugin = new SubagentPlugin(new RendezvousClient(expected: 3), explorerTools: []);

        var result = await plugin.ExploreManyAsync(["auth", "billing", "sessions"]);

        Assert.Equal(
            "## Exploration 1: auth\nreport on auth\n\n" +
            "## Exploration 2: billing\nreport on billing\n\n" +
            "## Exploration 3: sessions\nreport on sessions",
            result);
    }

    [Fact]
    public async Task LeavesOutToolsThatCanMutate()
    {
        var client = new ScriptedClient();
        var tools = new List<AIFunction>
        {
            AIFunctionFactory.Create(() => "contents", "read_file"),
            AIFunctionFactory.Create(() => "ran", "shell_run"),
        };
        var plugin = new SubagentPlugin(client, explorerTools: tools);

        await plugin.ExploreManyAsync(["where is X?"]);
        Assert.Equal(["read_file"], client.OfferedTools.Single());

        await plugin.ExploreAsync("where is X?");
        Assert.Contains("shell_run", client.OfferedTools[^1]);
    }

    [Fact]
    public async Task RefusesApprovalPromptsInsideTheRuns_ButTheCallerStillAsks()
    {
        var asked = 0;
        var approve = ApprovalScope.Guard((string _, string _) => { asked++; return Task.FromResult(true); });
        bool? answerInsideRun = null;
        var outsideRead = AIFunctionFactory.Create(async () =>
        {
            answerInsideRun = await approve("read_file", "/etc/hosts is outside the current sandbox");
            return answerInsideRun == true ? "contents" : "denied";
        }, "read_file");
        var client = new ScriptedClient(callFirst: "read_file");
        var plugin = new SubagentPlugin(client, explorerTools: [outsideRead]);

        await plugin.ExploreManyAsync(["read /etc/hosts"]);

        Assert.False(answerInsideRun);
        Assert.Equal(0, asked);
        Assert.True(await approve("read_file", "the caller's own request"));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task TagsEachRunsEventsWithItsOwnId()
    {
        var log = Path.Combine(_dir, "events.jsonl");
        var plugin = new SubagentPlugin(new RendezvousClient(expected: 2), explorerTools: [],
            eventEmitter: new EventEmitter(log), parentAgentName: "repl");

        await plugin.ExploreManyAsync(["a", "b"]);

        var starts = File.ReadAllLines(log)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("event_type").GetString() == EventTypes.SubagentStart)
            .Select(e => e.GetProperty("payload"))
            .ToList();
        Assert.Equal(["explore 1", "explore 2"], starts.Select(p => p.GetProperty("label").GetString()).Order());
        Assert.Equal(2, starts.Select(p => p.GetProperty("run").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task TakesOneToFourQueries()
    {
        var plugin = new SubagentPlugin(new ScriptedClient(), explorerTools: []);

        Assert.StartsWith("Give between 1 and 4", await plugin.ExploreManyAsync([]));
        Assert.StartsWith("Give between 1 and 4", await plugin.ExploreManyAsync(["1", "2", "3", "4", "5"]));
    }

    /// <summary>Answers each run with a report on its query, but only once every expected run is in flight.</summary>
    private sealed class RendezvousClient(int expected) : IChatClient
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var query = messages.Last(m => m.Role == ChatRole.User).Text;
            if (Interlocked.Increment(ref _arrived) == expected)
                _all.SetResult();
            await _all.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, $"report on {query}"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    /// <summary>Records the tools offered on each first request; optionally calls one tool once, then answers.</summary>
    private sealed class ScriptedClient(string? callFirst = null) : IChatClient
    {
        public List<List<string>> OfferedTools { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var started = list.All(m => m.Role != ChatRole.Tool);
            if (started)
                lock (OfferedTools)
                    OfferedTools.Add([.. (options?.Tools ?? []).Select(t => t.Name)]);
            var reply = callFirst is not null && started
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", callFirst, new Dictionary<string, object?>())])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
