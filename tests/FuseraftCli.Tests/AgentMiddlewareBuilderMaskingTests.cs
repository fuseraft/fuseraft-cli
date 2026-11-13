using fuseraft.Core.Models;
using fuseraft.Infrastructure.Agents;
using fuseraft.Infrastructure.Plugins;
using Microsoft.Extensions.AI;

namespace FuseraftCli.Tests;

/// <summary>Every tool result is masked on its way to the model, whichever tool produced it and however it's invoked.</summary>
public sealed class AgentMiddlewareBuilderMaskingTests
{
    private const string Name  = "FUSERAFT_TEST_INVOKER_API_KEY";
    private const string Value = "inv-test-0123456789abcdef";

    private static string? Text(object? result) =>
        result is System.Text.Json.JsonElement e ? e.GetString() : result as string;

    private static FunctionInvocationContext ContextFor(AIFunction function) =>
        new() { Function = function, Arguments = new AIFunctionArguments() };

    [Fact]
    public async Task MaskingInvoker_WithoutAnInvoker_MasksTheToolsOwnResult()
    {
        Environment.SetEnvironmentVariable(Name, Value);
        try
        {
            var tool = AIFunctionFactory.Create(() => $"KEY={Value}", "dump_env");

            var result = await AgentMiddlewareBuilder.MaskingInvoker(null)(ContextFor(tool), CancellationToken.None);

            Assert.Equal($"KEY={EnvSecretMasker.Placeholder}", Text(result));
        }
        finally { Environment.SetEnvironmentVariable(Name, null); }
    }

    [Fact]
    public async Task MaskingInvoker_MasksWhatACustomInvokerReturns()
    {
        Environment.SetEnvironmentVariable(Name, Value);
        try
        {
            var tool = AIFunctionFactory.Create(() => "unused", "noop");

            var result = await AgentMiddlewareBuilder.MaskingInvoker((_, _) => ValueTask.FromResult<object?>($"leak {Value}"))(
                ContextFor(tool), CancellationToken.None);

            Assert.Equal($"leak {EnvSecretMasker.Placeholder}", Text(result));
        }
        finally { Environment.SetEnvironmentVariable(Name, null); }
    }

    [Fact]
    public async Task BuiltClient_SendsTheModelAMaskedToolResult()
    {
        Environment.SetEnvironmentVariable(Name, Value);
        try
        {
            var model = new CallsOneToolClient("dump_env");
            var client = AgentMiddlewareBuilder.BuildEventEmitMiddleware(model, new AgentConfig { Name = "a" }, skillsProvider: null);
            var tool = AIFunctionFactory.Create(() => $"KEY={Value}", "dump_env");

            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [tool] });

            Assert.NotNull(model.ToolResult);
            Assert.DoesNotContain(Value, model.ToolResult);
            Assert.Contains(EnvSecretMasker.Placeholder, model.ToolResult);
        }
        finally { Environment.SetEnvironmentVariable(Name, null); }
    }

    // Asks for one tool call, then records the result it was sent back.
    private sealed class CallsOneToolClient(string toolName) : IChatClient
    {
        public string? ToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            if (result is null)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", toolName, new Dictionary<string, object?>())])));
            ToolResult = result.Result?.ToString();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
