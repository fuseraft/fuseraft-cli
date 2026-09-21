using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models.Config;

namespace FuseraftCli.Tests;

public sealed class ReplLimitsTests
{
    [Fact]
    public void NoConfig_UsesTheBuiltInDefaults()
    {
        var l = ReplLimits.From(null);

        Assert.Equal(0.75, l.AutoCompactThreshold);
        Assert.Equal(0.20, l.PreserveTailRatio);
        Assert.Equal(ReplTurn.MaxConsecutiveToolFailures, l.MaxConsecutiveToolFailures);
        Assert.Equal(ReplTurn.MaxConsecutiveIdenticalToolCalls, l.MaxIdenticalToolCalls);
        Assert.Equal(ReplTurn.SoftRepeatedToolCallThreshold, l.WarnIdenticalToolCalls);
        Assert.Equal(2, l.MaxStreamRetries);
    }

    [Fact]
    public void EmptyConfigSection_MatchesTheDefaults()
    {
        Assert.Equal(ReplLimits.Default, ReplLimits.From(new ReplDefaultsConfig()));
    }

    [Fact]
    public void ConfiguredValues_Apply()
    {
        var l = ReplLimits.From(new ReplDefaultsConfig
        {
            AutoCompactThreshold = 0.6, CompactPreserveTailRatio = 0.3, MaxConsecutiveToolFailures = 8,
            MaxIdenticalToolCalls = 12, WarnIdenticalToolCalls = 7, MaxStreamRetries = 4,
        });

        Assert.Equal(0.6, l.AutoCompactThreshold);
        Assert.Equal(0.3, l.PreserveTailRatio);
        Assert.Equal(8, l.MaxConsecutiveToolFailures);
        Assert.Equal(12, l.MaxIdenticalToolCalls);
        Assert.Equal(7, l.WarnIdenticalToolCalls);
        Assert.Equal(4, l.MaxStreamRetries);
    }

    [Fact]
    public void OutOfRangeValues_AreClampedSoAHandEditedFileCannotDisableASafetyRail()
    {
        var l = ReplLimits.From(new ReplDefaultsConfig
        {
            AutoCompactThreshold = 5, CompactPreserveTailRatio = 0, MaxConsecutiveToolFailures = 0,
            MaxIdenticalToolCalls = 1, MaxStreamRetries = 99,
        });

        Assert.Equal(ReplLimits.AutoCompactThresholdMax, l.AutoCompactThreshold);
        Assert.Equal(ReplLimits.PreserveTailRatioMin, l.PreserveTailRatio);
        Assert.Equal(ReplLimits.ToolFailuresMin, l.MaxConsecutiveToolFailures);
        Assert.Equal(ReplLimits.IdenticalCallsMin, l.MaxIdenticalToolCalls);
        Assert.Equal(ReplLimits.StreamRetriesMax, l.MaxStreamRetries);
    }

    [Theory]
    [InlineData(6, 6, 5)]   // warning at the cutoff itself is pulled below it
    [InlineData(4, 9, 3)]
    [InlineData(3, 3, 2)]   // smallest cutoff still leaves room for a warning
    [InlineData(10, 4, 4)]  // already below the cutoff — untouched
    public void TheWarningAlwaysLandsBeforeTheCutoff(int cutoff, int warn, int expectedWarn)
    {
        var l = ReplLimits.From(new ReplDefaultsConfig { MaxIdenticalToolCalls = cutoff, WarnIdenticalToolCalls = warn });

        Assert.Equal(cutoff, l.MaxIdenticalToolCalls);
        Assert.Equal(expectedWarn, l.WarnIdenticalToolCalls);
    }

    [Fact]
    public void LoweringTheCutoffAlone_PullsTheDefaultWarningDownWithIt()
    {
        var l = ReplLimits.From(new ReplDefaultsConfig { MaxIdenticalToolCalls = 3 });

        Assert.Equal(2, l.WarnIdenticalToolCalls);
    }
}
