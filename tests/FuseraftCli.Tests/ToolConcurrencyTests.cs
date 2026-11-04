using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

public sealed class ToolConcurrencyTests
{
    private static AIFunction Named(string name) => AIFunctionFactory.Create(() => name, name);

    [Theory]
    [InlineData("search_content")]
    [InlineData("git_diff")]
    [InlineData("subagent_explore")]
    [InlineData("http_get")]
    public void Reads_OverlapWithAnything(string name) =>
        Assert.Equal(ToolConcurrency.AnyLane, ToolConcurrency.ReadLane(Named(name)));

    [Fact]
    public void FileSystemReads_ShareALane()
    {
        var lane = ToolConcurrency.ReadLane(Named("read_file"));

        Assert.False(string.IsNullOrEmpty(lane));
        Assert.Equal(lane, ToolConcurrency.ReadLane(Named("list_files")));
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("shell_run")]
    [InlineData("git_commit")]
    [InlineData("subagent_delegate")]
    [InlineData("repl_session_compact_context")]
    [InlineData("some_mcp_tool")]
    public void AnythingElse_RunsAlone(string name) =>
        Assert.Null(ToolConcurrency.ReadLane(Named(name)));

    [Fact]
    public async Task OneAtATime_GatesOnlyTheNamedTools()
    {
        var inside = 0;
        var most   = 0;
        AIFunction Slow(string name) => AIFunctionFactory.Create(async () =>
        {
            var now = Interlocked.Increment(ref inside);
            if (now > Volatile.Read(ref most)) Volatile.Write(ref most, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref inside);
            return name;
        }, name);

        var tools = ToolConcurrency.OneAtATime([Slow("shell_run"), Slow("shell_run_script"), Slow("shell_which")],
            new HashSet<string> { "shell_run", "shell_run_script" });
        await Task.WhenAll(tools.Take(2).Select(t => t.InvokeAsync().AsTask()));

        Assert.Equal(1, most);
        Assert.Equal("shell_which", tools[2].Name);
        Assert.IsNotType<DelegatingAIFunction>(tools[2], exactMatch: false);
    }
}
