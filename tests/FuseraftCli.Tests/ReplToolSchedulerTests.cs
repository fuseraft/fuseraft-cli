using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using fuseraft.Cli.Commands.Repl;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="ReplToolScheduler"/> driven through a real <see cref="FunctionInvokingChatClient"/> with
/// concurrent invocation on, wired as <c>ReplFactory.BuildClient</c> wires it (under
/// <see cref="ReplToolLoopGuard"/>). Overlap is proven with barriers rather than timings.
/// </summary>
public sealed class ReplToolSchedulerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // One response with every call at once, then a closing text reply.
    private sealed class OneRoundClient(params string[] toolNames) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (messages.Any(m => m.Role == ChatRole.Tool))
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            var calls = toolNames.Select((name, i) => (AIContent)new FunctionCallContent($"c{i}", name)).ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, calls)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static async Task<List<FunctionResultContent>> RunRoundAsync(IEnumerable<AIFunction> tools, params string[] calls)
    {
        var client = new FunctionInvokingChatClient(new OneRoundClient(calls))
        {
            AllowConcurrentInvocation = true,
            FunctionInvoker = new ReplToolLoopGuard(next: new ReplToolScheduler().InvokeAsync).InvokeAsync,
        };
        var response = await client.GetResponseAsync("go", new ChatOptions { Tools = [.. tools] });
        return [.. response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()];
    }

    // A tool that records when it starts and ends.
    private static AIFunction Logged(string name, ConcurrentQueue<string> log, Func<Task>? body = null) =>
        AIFunctionFactory.Create(async () =>
        {
            log.Enqueue($"{name}+");
            await Task.Yield();
            if (body is not null) await body();
            log.Enqueue($"{name}-");
            return name;
        }, name);

    [Fact]
    public async Task ReadOnlyCalls_RunAtTheSameTime()
    {
        // Each read waits until both are inside; run one at a time, the first would time out.
        using var both = new CountdownEvent(2);
        AIFunction Read(string name) => AIFunctionFactory.Create(async () =>
        {
            both.Signal();
            await Task.Run(() => both.Wait(Wait));
            return both.IsSet ? "overlapped" : "alone";
        }, name);

        var results = await RunRoundAsync([Read("search_content"), Read("git_log")], "search_content", "git_log");

        Assert.All(results, r => Assert.Equal("overlapped", r.Result?.ToString()));
    }

    [Fact]
    public async Task SynchronousReads_StillOverlap()
    {
        using var both = new CountdownEvent(2);
        AIFunction Read(string name) => AIFunctionFactory.Create(() =>
        {
            both.Signal();
            return both.Wait(Wait) ? "overlapped" : "alone";
        }, name);

        var results = await RunRoundAsync([Read("search_symbol"), Read("search_callers")], "search_symbol", "search_callers");

        Assert.All(results, r => Assert.Equal("overlapped", r.Result?.ToString()));
    }

    [Fact]
    public async Task AWrite_WaitsForEarlierCalls_AndHoldsUpLaterOnes()
    {
        var log = new ConcurrentQueue<string>();
        var tools = new[] { Logged("search_content", log), Logged("write_file", log), Logged("git_status", log) };

        await RunRoundAsync(tools, "search_content", "write_file", "git_status");

        Assert.Equal(["search_content+", "search_content-", "write_file+", "write_file-", "git_status+", "git_status-"], log);
    }

    [Fact]
    public async Task FileSystemReads_TakeTurns()
    {
        var inside = 0;
        var most   = 0;
        AIFunction Read(string name) => AIFunctionFactory.Create(async () =>
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref most, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref inside);
            return name;
        }, name);

        await RunRoundAsync([Read("read_file"), Read("list_files")], "read_file", "list_files", "read_file");

        Assert.Equal(1, most);
    }

    [Fact]
    public async Task AnUnknownTool_DoesNotHoldUpTheRound()
    {
        var log = new ConcurrentQueue<string>();

        var results = await RunRoundAsync([Logged("search_content", log)], "no_such_tool", "search_content")
            .WaitAsync(Wait);

        Assert.Equal(2, results.Count);
        Assert.Equal(["search_content+", "search_content-"], log);
    }

    [Fact]
    public async Task Results_ComeBackInCallOrder()
    {
        var slowGate = new TaskCompletionSource();
        var tools = new[]
        {
            AIFunctionFactory.Create(async () => { await slowGate.Task; return "slow"; }, "search_content"),
            AIFunctionFactory.Create(() => { slowGate.SetResult(); return "fast"; }, "git_log"),
        };

        // The slow read finishes only once the fast one runs, so run one at a time this would never end.
        var results = await RunRoundAsync(tools, "search_content", "git_log").WaitAsync(Wait);

        Assert.Equal(["c0", "c1"], results.Select(r => r.CallId));
        Assert.Equal(["slow", "fast"], results.Select(r => r.Result?.ToString()));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
