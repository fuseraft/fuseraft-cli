using fuseraft.Infrastructure.Agents;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="AdaptiveTrimTracker"/> — the signal
/// <see cref="fuseraft.Infrastructure.Agents"/>'s adaptive context-trim retry uses to tell
/// <c>CompactionCoordinator</c> that a provider call only survived by truncating content, so a
/// real compaction should run before the next turn instead of letting the same oversized
/// history recur.
/// </summary>
public sealed class AdaptiveTrimTrackerTests
{
    [Fact]
    public void ConsumeTrim_NeverRecorded_ReturnsFalse()
    {
        var tracker = new AdaptiveTrimTracker();
        Assert.False(tracker.ConsumeTrim("Developer"));
    }

    [Fact]
    public void ConsumeTrim_AfterRecordTrim_ReturnsTrueThenFalse()
    {
        var tracker = new AdaptiveTrimTracker();
        tracker.RecordTrim("Developer");

        Assert.True(tracker.ConsumeTrim("Developer"));
        Assert.False(tracker.ConsumeTrim("Developer")); // consuming clears the flag
    }

    [Fact]
    public void RecordTrim_CalledTwiceBeforeConsume_IsStillOneFlag()
    {
        var tracker = new AdaptiveTrimTracker();
        tracker.RecordTrim("Developer");
        tracker.RecordTrim("Developer");

        Assert.True(tracker.ConsumeTrim("Developer"));
        Assert.False(tracker.ConsumeTrim("Developer"));
    }

    [Fact]
    public void Tracking_IsPerAgent_IndependentOfOtherAgents()
    {
        var tracker = new AdaptiveTrimTracker();
        tracker.RecordTrim("Developer");

        Assert.False(tracker.ConsumeTrim("Reviewer"));
        Assert.True(tracker.ConsumeTrim("Developer"));
    }

    [Fact]
    public void AgentNames_AreCaseInsensitive()
    {
        var tracker = new AdaptiveTrimTracker();
        tracker.RecordTrim("Developer");

        Assert.True(tracker.ConsumeTrim("developer"));
    }
}
