using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="NotifyingAIFunction"/> — the <see cref="DelegatingAIFunction"/> proxy
/// that fires a callback before each tool invocation and rejects calls missing a required
/// parameter before they ever reach the inner function.
/// </summary>
public sealed class NotifyingAIFunctionTests
{
    private static AIFunction MakeInner() =>
        AIFunctionFactory.Create(
            (string requiredArg, string? optionalArg = null) => $"{requiredArg}:{optionalArg ?? "none"}",
            "some_tool");

    [Fact]
    public async Task InvokeAsync_AllRequiredParamsPresent_InvokesInnerAndReturnsResult()
    {
        var filter = new NotifyingAIFunction(MakeInner(), "agent-a", (_, _, _) => Task.CompletedTask);

        var result = await filter.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["requiredArg"] = "hello" }));

        // AIFunctionFactory-built functions hand back a JsonElement, not the raw CLR string
        // the delegate returned — ToString() gets the text back either way.
        Assert.Equal("hello:none", result?.ToString());
    }

    [Fact]
    public async Task InvokeAsync_MissingRequiredParam_ReturnsStructuredErrorWithoutCallingInner()
    {
        var innerCalled = false;
        var inner = AIFunctionFactory.Create(
            (string requiredArg) => { innerCalled = true; return requiredArg; },
            "some_tool");
        var filter = new NotifyingAIFunction(inner, "agent-a", (_, _, _) => Task.CompletedTask);

        var result = await filter.InvokeAsync(new AIFunctionArguments());

        var text = Assert.IsType<string>(result);
        Assert.StartsWith("[ERROR]", text);
        Assert.Contains("requiredArg", text);
        Assert.False(innerCalled);
    }

    [Fact]
    public async Task InvokeAsync_MissingOptionalParam_DoesNotError()
    {
        var filter = new NotifyingAIFunction(MakeInner(), "agent-a", (_, _, _) => Task.CompletedTask);

        var result = await filter.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["requiredArg"] = "hi" }));

        Assert.Equal("hi:none", result?.ToString());
    }

    [Fact]
    public async Task InvokeAsync_OnBeforeInvoke_FiresWithAgentNameToolNameAndSummarizedArgs()
    {
        string? seenAgent = null, seenTool = null, seenArgs = null;
        var filter = new NotifyingAIFunction(MakeInner(), "agent-x", (agent, tool, args) =>
        {
            seenAgent = agent;
            seenTool  = tool;
            seenArgs  = args;
            return Task.CompletedTask;
        });

        await filter.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["requiredArg"] = "v" }));

        Assert.Equal("agent-x", seenAgent);
        Assert.Equal("some_tool", seenTool);
        Assert.NotNull(seenArgs);
    }

    [Fact]
    public async Task InvokeAsync_OnBeforeInvoke_FiresEvenWhenValidationSubsequentlyFails()
    {
        // The notify callback (e.g. loop-guard bookkeeping) must still observe the attempted
        // call even when it's about to be rejected for missing parameters — the model's attempt
        // still counts toward repeated-call/loop tracking.
        var fired = false;
        var inner = AIFunctionFactory.Create((string requiredArg) => requiredArg, "some_tool");
        var filter = new NotifyingAIFunction(inner, "agent-a", (_, _, _) => { fired = true; return Task.CompletedTask; });

        await filter.InvokeAsync(new AIFunctionArguments());

        Assert.True(fired);
    }
}
