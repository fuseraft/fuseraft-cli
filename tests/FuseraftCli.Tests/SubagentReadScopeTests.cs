using System.Text.Json;
using Microsoft.Extensions.AI;
using fuseraft.Core;
using fuseraft.Core.Events;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// A subagent shares the parent's <see cref="FileSystemPlugin"/>, but reads in its own
/// <see cref="ReadScope"/>: what it reads must not make the parent's next read of the same file come
/// back as "already read — content is in context", since that content never reached the parent.
/// </summary>
public sealed class SubagentReadScopeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-read-scope-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ParentReadsAFileTheSubagentAlreadyRead_GetsTheContent()
    {
        var path = Path.Combine(_dir, "Program.cs");
        await File.WriteAllTextAsync(path, "class Program {}");
        var fs = new FileSystemPlugin(sandboxRoot: _dir);
        var subagent = new SubagentPlugin(new ReadingClient(path, times: 1), explorerTools: ReadTool(fs));

        await subagent.ExploreAsync("what does Program.cs do?");

        Assert.Contains("class Program {}", await fs.ReadFileAsync(path));
    }

    [Fact]
    public async Task SubagentReadingTheSameFileTwice_StillGetsTheHint()
    {
        var path = Path.Combine(_dir, "Program.cs");
        await File.WriteAllTextAsync(path, "class Program {}");
        var fs = new FileSystemPlugin(sandboxRoot: _dir);
        var client = new ReadingClient(path, times: 2);
        var subagent = new SubagentPlugin(client, explorerTools: ReadTool(fs));

        await subagent.ExploreAsync("what does Program.cs do?");

        Assert.Contains("class Program {}", client.Results[0]);
        Assert.Contains("already read this turn", client.Results[1]);
    }

    [Fact]
    public async Task ParentReadsDoNotLeakIntoALaterSubagentRun()
    {
        var path = Path.Combine(_dir, "Program.cs");
        await File.WriteAllTextAsync(path, "class Program {}");
        var fs = new FileSystemPlugin(sandboxRoot: _dir);
        await fs.ReadFileAsync(path);
        var client = new ReadingClient(path, times: 1);
        var subagent = new SubagentPlugin(client, explorerTools: ReadTool(fs));

        await subagent.ExploreAsync("what does Program.cs do?");

        Assert.Contains("class Program {}", client.Results.Single());
    }

    [Fact]
    public async Task ASingleRunsEventsAreLabelledWithItsMode()
    {
        var log = Path.Combine(_dir, "events.jsonl");
        var subagent = new SubagentPlugin(new ReadingClient(Path.Combine(_dir, "x"), times: 0), explorerTools: [],
            eventEmitter: new EventEmitter(log), parentAgentName: "repl");

        await subagent.ExploreAsync("anything");

        var start = File.ReadAllLines(log)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Single(e => e.GetProperty("event_type").GetString() == EventTypes.SubagentStart);
        Assert.Equal("explore", start.GetProperty("payload").GetProperty("label").GetString());
    }

    [Fact]
    public void ReplOrientation_ListsOnlyProjectFoldersThatExist()
    {
        // The test runner's working directory has no .fuseraft/ folder.
        var all = FuseraftPaths.BuildFolderOrientationBlock("s", includeLogs: false, includeInfrastructure: false);
        var existing = FuseraftPaths.BuildFolderOrientationBlock("s", includeLogs: false, includeInfrastructure: false,
            onlyExistingProjectFiles: true);

        Assert.Contains(".fuseraft/tests/", all);
        Assert.DoesNotContain(".fuseraft/tests/", existing);
        Assert.DoesNotContain(".fuseraft/docs/", existing);
    }

    private static List<AIFunction> ReadTool(FileSystemPlugin fs) =>
        [.. PluginRegistry.GetFunctionsFromObject(fs).Where(f => f.Name == "read_file")];

    /// <summary>Calls read_file on <paramref name="path"/> <paramref name="times"/> times, one call per round, then answers.</summary>
    private sealed class ReadingClient(string path, int times) : IChatClient
    {
        public List<string> Results { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var results = list.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();
            Results.Clear();
            Results.AddRange(results.Select(r => r.Result?.ToString() ?? ""));
            var reply = results.Count < times
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{results.Count}", "read_file",
                    new Dictionary<string, object?> { ["path"] = path })])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
