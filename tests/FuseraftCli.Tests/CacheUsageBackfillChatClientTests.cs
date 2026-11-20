using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Chat;

namespace FuseraftCli.Tests;

public class CacheUsageBackfillChatClientTests
{
    // LiteLLM's usage block for Claude on Bedrock: the OpenAI fields plus Anthropic's cache-write count.
    private const string LiteLlmUsage = """
        {"prompt_tokens":60000,"completion_tokens":200,"total_tokens":60200,
         "prompt_tokens_details":{"cached_tokens":52000},
         "cache_creation_input_tokens":7500,"cache_read_input_tokens":52000}
        """;

    [Fact]
    public async Task Streaming_GatewayUsage_BackfillsCacheReadsAndWrites()
    {
        var usage = await StreamedUsageAsync(LiteLlmUsage);

        Assert.Equal(52_000, usage.CacheReadTokens());
        Assert.Equal(7_500, usage.CacheWriteTokens());
        // Still read as an OpenAI-shaped count: the prompt total includes the cache.
        Assert.Equal(60_000, usage.PromptTokens());
        Assert.Equal(8_000, usage.UncachedInputTokens());
    }

    [Fact]
    public async Task Streaming_NoCacheWriteField_LeavesWritesAtZero()
    {
        var usage = await StreamedUsageAsync("""
            {"prompt_tokens":1000,"completion_tokens":5,"total_tokens":1005,"prompt_tokens_details":{"cached_tokens":900}}
            """);

        Assert.Equal(900, usage.CacheReadTokens());
        Assert.Equal(0, usage.CacheWriteTokens());
        Assert.False(usage.AdditionalCounts!.ContainsKey(CacheUsageBackfillChatClient.CacheWriteInputTokensKey));
    }

    private static async Task<UsageDetails> StreamedUsageAsync(string usageJson)
    {
        var raw = ModelReaderWriter.Read<OpenAI.Chat.ChatTokenUsage>(BinaryData.FromString(usageJson))!;
        var client = new CacheUsageBackfillChatClient(new UsageClient(new UsageContent(new UsageDetails
        {
            InputTokenCount = raw.InputTokenCount,
            OutputTokenCount = raw.OutputTokenCount,
        }) { RawRepresentation = raw }));

        UsageDetails? found = null;
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            found = update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details ?? found;
        return found!;
    }

    private sealed class UsageClient(UsageContent usage) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [usage]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
