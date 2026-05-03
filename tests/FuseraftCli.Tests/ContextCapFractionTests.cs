using Microsoft.Extensions.AI;
using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests that <see cref="ContextWindowConfig.ContextCapFraction"/> is stored and accessible,
/// and that <see cref="ContextWindowFilter"/> continues to apply <see cref="ContextWindowConfig.MaxTailMessages"/>
/// as the hard cap regardless of whether <see cref="ContextWindowConfig.ContextCapFraction"/> is set.
/// The fraction itself governs a warning signal emitted by the orchestrator; the filter does not
/// change its trim behaviour based on the fraction.
/// </summary>
public sealed class ContextCapFractionTests
{
    // -----------------------------------------------------------------------
    // ContextWindowConfig — field round-trip
    // -----------------------------------------------------------------------

    [Fact]
    public void ContextCapFraction_DefaultIsZero()
    {
        var config = new ContextWindowConfig();
        Assert.Equal(0.0, config.ContextCapFraction);
    }

    [Fact]
    public void ContextCapFraction_CanBeSet()
    {
        var config = new ContextWindowConfig { ContextCapFraction = 0.4 };
        Assert.Equal(0.4, config.ContextCapFraction);
    }

    // -----------------------------------------------------------------------
    // ContextWindowFilter — hard cap still enforced when fraction is set
    // -----------------------------------------------------------------------

    [Fact]
    public void Filter_WithBothCaps_HardCapEnforced()
    {
        // 15 messages, MaxTailMessages = 10, ContextCapFraction = 0.4 (soft threshold = 4).
        // The filter keeps the hard cap of 10; fraction is a signal only.
        var history = Enumerable.Range(1, 15)
            .Select(i => new ChatMessage(ChatRole.User, $"msg {i}"))
            .ToList();

        var config = new ContextWindowConfig { MaxTailMessages = 10, ContextCapFraction = 0.4 };

        var result = ContextWindowFilter.Apply(history, config);

        Assert.Equal(10, result.Count);
        Assert.Equal("msg 6", result[0].Contents.OfType<TextContent>().First().Text);
    }

    [Fact]
    public void Filter_BelowHardCap_AllMessagesRetained()
    {
        var history = Enumerable.Range(1, 3)
            .Select(i => new ChatMessage(ChatRole.User, $"msg {i}"))
            .ToList();

        var config = new ContextWindowConfig { MaxTailMessages = 10, ContextCapFraction = 0.4 };

        var result = ContextWindowFilter.Apply(history, config);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Filter_NullConfig_ReturnsAllMessages()
    {
        var history = Enumerable.Range(1, 5)
            .Select(i => new ChatMessage(ChatRole.User, $"msg {i}"))
            .ToList();

        var result = ContextWindowFilter.Apply(history, null);

        Assert.Equal(5, result.Count);
    }

}
