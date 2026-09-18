using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="CompactionPlugin"/> — the single-tool trigger
/// <see cref="fuseraft.Cli.SessionRunner"/> watches for to fire an on-demand compaction flush.
/// </summary>
public sealed class CompactionPluginTests
{
    [Fact]
    public void CompactConversation_ReturnsExpectedSentinel()
    {
        var plugin = new CompactionPlugin();

        var result = plugin.CompactConversation();

        Assert.Equal("COMPACT_REQUESTED", result);
    }

    [Fact]
    public void CompactConversation_ReturnsSameSentinel_OnRepeatedCalls()
    {
        var plugin = new CompactionPlugin();

        // SessionRunner matches on the exact string every time a turn completes —
        // it must not vary call to call (e.g. no per-call state, no randomness).
        Assert.Equal(plugin.CompactConversation(), plugin.CompactConversation());
    }

    [Fact]
    public void Constants_AreCorrect()
    {
        Assert.Equal("Compaction",          CompactionPlugin.PluginName);
        Assert.Equal("compact_conversation", CompactionPlugin.FunctionName);
    }
}
