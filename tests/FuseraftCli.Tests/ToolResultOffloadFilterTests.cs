using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Infrastructure.Tools;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ToolResultOffloadFilter"/> — the <see cref="DelegatingAIFunction"/>
/// proxy that offloads oversized tool results to <see cref="ToolResultArtifactStore"/> instead
/// of letting them enter conversation history verbatim.
/// </summary>
public sealed class ToolResultOffloadFilterTests : IDisposable
{
    private readonly string _artifactsDir;

    public ToolResultOffloadFilterTests()
    {
        _artifactsDir = Path.Combine(Path.GetTempPath(), "fuseraft_offload_tests_" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_artifactsDir))
            Directory.Delete(_artifactsDir, recursive: true);
    }

    private static AIFunction MakeInner(object? returnValue, string name = "test_tool") =>
        AIFunctionFactory.Create((string arg) => returnValue, name);

    [Fact]
    public async Task InvokeAsync_SmallResult_PassesThroughUnchanged()
    {
        var store  = new ToolResultArtifactStore(_artifactsDir) { ThresholdChars = 1000 };
        var inner  = MakeInner("a short result");
        var filter = new ToolResultOffloadFilter(inner, store);

        var result = await filter.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["arg"] = "x" }));

        // AIFunctionFactory-built functions hand back a JsonElement, not the raw CLR string
        // InnerFunction's delegate returned — ToString() gets the text back either way.
        Assert.Equal("a short result", result?.ToString());
        Assert.Empty(Directory.Exists(_artifactsDir) ? Directory.GetFiles(_artifactsDir) : []);
    }

    [Fact]
    public async Task InvokeAsync_LargeResult_OffloadedWithReferenceStub()
    {
        var store  = new ToolResultArtifactStore(_artifactsDir) { ThresholdChars = 10 };
        var inner  = MakeInner(new string('x', 500), name: "big_tool");
        var filter = new ToolResultOffloadFilter(inner, store);

        var result = await filter.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["arg"] = "x" }));

        var stub = Assert.IsType<string>(result);
        Assert.Contains("offloaded", stub);
        Assert.Contains("big_tool", stub);
        Assert.DoesNotContain(new string('x', 500), stub);
        Assert.Single(Directory.GetFiles(_artifactsDir));
    }

    [Fact]
    public async Task InvokeAsync_NonStringResult_PassesThroughUnchanged()
    {
        // Only string (or JSON-string-element) results are eligible for offload — a tool that
        // legitimately returns a non-string object must be untouched.
        var store  = new ToolResultArtifactStore(_artifactsDir) { ThresholdChars = 1 };
        var inner  = MakeInner(42);
        var filter = new ToolResultOffloadFilter(inner, store);

        var result = await filter.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["arg"] = "x" }));

        Assert.Equal("42", result?.ToString());
    }

    [Fact]
    public async Task InvokeAsync_NoArtifactsDir_NeverOffloadsRegardlessOfSize()
    {
        var store  = new ToolResultArtifactStore(artifactsDir: null) { ThresholdChars = 1 };
        var inner  = MakeInner(new string('y', 5000));
        var filter = new ToolResultOffloadFilter(inner, store);

        var result = await filter.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["arg"] = "x" }));

        Assert.Equal(new string('y', 5000), result?.ToString());
    }
}
