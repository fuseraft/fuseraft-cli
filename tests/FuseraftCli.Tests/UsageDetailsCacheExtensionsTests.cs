using Microsoft.Extensions.AI;
using fuseraft.Infrastructure.Chat;

namespace FuseraftCli.Tests;

public class UsageDetailsCacheExtensionsTests
{
    [Fact]
    public void UncachedInputTokens_OpenAiCompatible_SubtractsCacheReadsFromTheWholePrompt()
    {
        var usage = new UsageDetails
        {
            InputTokenCount  = 60_000,
            AdditionalCounts = new() { [CacheUsageBackfillChatClient.CacheReadInputTokensKey] = 58_000 },
        };

        Assert.Equal(2_000, usage.UncachedInputTokens());
    }

    [Fact]
    public void UncachedInputTokens_Anthropic_InputAlreadyExcludesCache_AddsCacheWrites()
    {
        // Anthropic.SDK's shape: input_tokens excludes cache reads and writes, and both keys are
        // always present.
        var usage = new UsageDetails
        {
            InputTokenCount  = 300,
            AdditionalCounts = new()
            {
                ["CacheCreationInputTokens"] = 1_200,
                [CacheUsageBackfillChatClient.CacheReadInputTokensKey] = 58_000,
            },
        };

        Assert.Equal(1_500, usage.UncachedInputTokens());
    }

    [Fact]
    public void UncachedInputTokens_NoCacheReported_IsTheWholeInput()
    {
        Assert.Equal(5_000, new UsageDetails { InputTokenCount = 5_000 }.UncachedInputTokens());
        Assert.Equal(0, ((UsageDetails?)null).UncachedInputTokens());
    }
}
