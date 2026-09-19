namespace FuseraftCli.Tests;

public sealed class ToolCallCycleDetectorTests
{
    private static ToolCallCycleDetector Feed(params string[] signatures)
    {
        var d = new ToolCallCycleDetector();
        foreach (var s in signatures) d.Observe(s);
        return d;
    }

    private static string[] Alternate(int calls, string a = "A", string b = "B") =>
        Enumerable.Range(0, calls).Select(i => i % 2 == 0 ? a : b).ToArray();

    [Fact]
    public void IdenticalCalls_AreNotAnAlternation()
    {
        var d = new ToolCallCycleDetector();
        for (var i = 0; i < 12; i++)
            Assert.Equal(ToolCallCycleVerdict.None, d.Observe("A"));

        Assert.Equal(0, d.LastLength);
    }

    [Fact]
    public void FirstCall_HasNoAlternation()
    {
        var d = Feed("A");

        Assert.Equal(0, d.LastLength);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(7, 7)]
    public void AlternatingRun_LengthCountsCallsInTheRun(int calls, int expected)
    {
        var d = Feed(Alternate(calls));

        Assert.Equal(expected, d.LastLength);
    }

    [Fact]
    public void SoftFiresExactlyOnce_AsTheRunClimbsThroughSoftThreshold()
    {
        var d = new ToolCallCycleDetector();
        var verdicts = Alternate(9).Select(d.Observe).ToArray();

        Assert.Equal(ToolCallCycleVerdict.Soft, verdicts[ToolCallCycleDetector.SoftThreshold - 1]);
        Assert.Equal(1, verdicts.Count(v => v == ToolCallCycleVerdict.Soft));
        Assert.All(verdicts.Take(ToolCallCycleDetector.SoftThreshold - 1), v => Assert.Equal(ToolCallCycleVerdict.None, v));
    }

    [Fact]
    public void HardFiresAtThresholdAndOnEveryCallAfter_EvenAsTheWindowSlides()
    {
        var d = new ToolCallCycleDetector();
        var verdicts = Alternate(30).Select(d.Observe).ToArray();

        Assert.All(verdicts.Skip(ToolCallCycleDetector.HardThreshold - 1),
            v => Assert.Equal(ToolCallCycleVerdict.Hard, v));
        Assert.Equal(ToolCallCycleVerdict.None, verdicts[ToolCallCycleDetector.HardThreshold - 2]);   // 9th call: past soft, short of hard
    }

    [Fact]
    public void DifferentCallInTheMiddle_BreaksTheRun()
    {
        var d = Feed("A", "B", "A", "B", "A", "C");

        Assert.Equal(2, d.LastLength);   // C broke the A/B pattern; only the trailing (A, C) pair remains
        Assert.Equal(ToolCallCycleVerdict.None, d.Observe("A"));
    }

    [Fact]
    public void ThreeWayRotation_IsNotAnAlternation()
    {
        var d = new ToolCallCycleDetector();
        var verdicts = Enumerable.Range(0, 24).Select(i => d.Observe(new[] { "A", "B", "C" }[i % 3])).ToArray();

        Assert.All(verdicts, v => Assert.Equal(ToolCallCycleVerdict.None, v));
        Assert.Equal(2, d.LastLength);
    }

    [Fact]
    public void AlternationAfterAnIdenticalPrefix_IsMeasuredFromTheAlternatingTailOnly()
    {
        var d = Feed("X", "X", "X", "A", "B", "A", "B");

        Assert.Equal(4, d.LastLength);
    }

    [Fact]
    public void TailOfTwoIdenticalCalls_ResetsTheRunToZero()
    {
        var d = Feed("A", "B", "A", "B", "B");

        Assert.Equal(0, d.LastLength);
    }

    [Fact]
    public void RunCanStartAgainAfterBeingBroken_AndFireSoftAgain()
    {
        var d = new ToolCallCycleDetector();
        foreach (var s in Alternate(6)) d.Observe(s);
        d.Observe("Z");

        var verdicts = Alternate(6, "P", "Q").Select(d.Observe).ToArray();

        Assert.Equal(ToolCallCycleVerdict.Soft, verdicts[^1]);
    }

    [Fact]
    public void Reset_ForgetsHistory()
    {
        var d = Feed(Alternate(9));
        d.Reset();

        Assert.Equal(0, d.LastLength);
        Assert.Equal(ToolCallCycleVerdict.None, d.Observe("A"));
        Assert.Equal(ToolCallCycleVerdict.None, d.Observe("B"));
        Assert.Equal(2, d.LastLength);
    }
}
