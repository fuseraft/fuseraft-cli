using Microsoft.Extensions.AI;
using fuseraft.Core.Events;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ToolResultLoggingFilter"/> — the transparent event-emitting proxy
/// wrapped around every tool call. Event emission is fire-and-forget (<c>_ = emitter.EmitAsync</c>),
/// so assertions on emitted content poll the JSONL log file briefly rather than racing the write.
/// </summary>
public sealed class ToolResultLoggingFilterTests : IDisposable
{
    private readonly string _logPath;
    private readonly EventEmitter _emitter;

    public ToolResultLoggingFilterTests()
    {
        _logPath = Path.Combine(Path.GetTempPath(), "fuseraft_tool_log_tests_" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");
        _emitter = new EventEmitter(_logPath);
    }

    public void Dispose()
    {
        if (File.Exists(_logPath))
            File.Delete(_logPath);
    }

    // ToolResultLoggingFilter is `internal` but directly constructible here: the main
    // assembly grants InternalsVisibleTo to this test assembly.
    private static AIFunction Wrap(AIFunction inner, EventEmitter emitter) =>
        new ToolResultLoggingFilter(inner, emitter);

    // Every invocation emits exactly two fire-and-forget events: tool_call first, then exactly
    // one outcome event (tool_result/tool_error/tool_timeout). Returning as soon as the file has
    // *any* content is a race — the tool_call line alone can land, and win the poll, before the
    // outcome line the assertions actually care about has been appended. Wait for both lines.
    private async Task<string> ReadLogAsync(int minLines = 2)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(_logPath))
            {
                var text = await File.ReadAllTextAsync(_logPath);
                var lineCount = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                if (lineCount >= minLines) return text;
            }
            await Task.Delay(20);
        }
        return File.Exists(_logPath) ? await File.ReadAllTextAsync(_logPath) : string.Empty;
    }

    [Fact]
    public async Task InvokeAsync_SuccessfulCall_ReturnsInnerResultUnchanged()
    {
        var inner  = AIFunctionFactory.Create((string x) => $"echo:{x}", "my_tool");
        var filter = Wrap(inner, _emitter);

        var result = await filter.InvokeAsync(new AIFunctionArguments { ["x"] = "hello" });

        // AIFunctionFactory-built functions hand back a JsonElement, not the raw CLR string
        // the delegate returned — ToString() gets the text back either way.
        Assert.Equal("echo:hello", result?.ToString());
    }

    [Fact]
    public async Task InvokeAsync_SuccessfulCall_EmitsToolCallAndToolResultEvents()
    {
        var inner  = AIFunctionFactory.Create((string x) => "ok", "my_tool");
        var filter = Wrap(inner, _emitter);

        await filter.InvokeAsync(new AIFunctionArguments { ["x"] = "hello" });

        var log = await ReadLogAsync();
        Assert.Contains($"\"event_type\":\"{EventTypes.ToolCall}\"", log);
        Assert.Contains($"\"event_type\":\"{EventTypes.ToolResult}\"", log);
        Assert.DoesNotContain($"\"event_type\":\"{EventTypes.ToolError}\"", log);
    }

    [Fact]
    public async Task InvokeAsync_InnerThrows_EmitsToolErrorAndRethrows()
    {
        var inner = AIFunctionFactory.Create(string () => throw new InvalidOperationException("boom"), "my_tool");
        var filter = Wrap(inner, _emitter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => filter.InvokeAsync().AsTask());

        var log = await ReadLogAsync();
        Assert.Contains($"\"event_type\":\"{EventTypes.ToolError}\"", log);
        Assert.Contains("boom", log);
    }

    [Fact]
    public async Task InvokeAsync_ResultStartsWithTimeoutMarker_EmitsToolTimeoutNotToolResult()
    {
        var inner  = AIFunctionFactory.Create(() => "[TIMEOUT] exceeded 30s", "my_tool");
        var filter = Wrap(inner, _emitter);

        await filter.InvokeAsync();

        var log = await ReadLogAsync();
        Assert.Contains($"\"event_type\":\"{EventTypes.ToolTimeout}\"", log);
        Assert.DoesNotContain($"\"event_type\":\"{EventTypes.ToolResult}\"", log);
    }

    [Theory]
    [InlineData("[ERROR] something went wrong")]
    [InlineData("[EXIT 1] command failed")]
    public async Task InvokeAsync_ResultLooksLikeError_EmitsToolErrorNotToolResult(string errorResult)
    {
        var inner  = AIFunctionFactory.Create(() => errorResult, "my_tool");
        var filter = Wrap(inner, _emitter);

        await filter.InvokeAsync();

        var log = await ReadLogAsync();
        Assert.Contains($"\"event_type\":\"{EventTypes.ToolError}\"", log);
        Assert.DoesNotContain($"\"event_type\":\"{EventTypes.ToolResult}\"", log);
    }

    [Fact]
    public async Task InvokeAsync_LongErrorResult_TruncatedInEmittedEvent()
    {
        var longError = "[ERROR] " + new string('x', 1000);
        var inner     = AIFunctionFactory.Create(() => longError, "my_tool");
        var filter    = Wrap(inner, _emitter);

        await filter.InvokeAsync();

        var log = await ReadLogAsync();
        Assert.Contains("chars truncated", log);
    }

    [Fact]
    public async Task InvokeAsync_ShellRunTool_IncludesOutputField()
    {
        var inner  = AIFunctionFactory.Create(() => "some output", "shell_run");
        var filter = Wrap(inner, _emitter);

        await filter.InvokeAsync();

        var log = await ReadLogAsync();
        Assert.Contains("\"output\":\"some output\"", log);
    }

    [Fact]
    public async Task InvokeAsync_NonShellTool_OmitsOutputField()
    {
        var inner  = AIFunctionFactory.Create(() => "some output", "read_file");
        var filter = Wrap(inner, _emitter);

        await filter.InvokeAsync();

        var log = await ReadLogAsync();
        Assert.DoesNotContain("\"output\":\"some output\"", log);
    }
}
