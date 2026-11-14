using fuseraft.Cli.Commands.Repl;
using fuseraft.Core;
using fuseraft.Core.Models.Config;

namespace FuseraftCli.Tests;

/// <summary>
/// Within a turn, older tool results are elided only as the request nears the REPL's own budget for the model, not
/// at a fixed 200K chars: with grok-4.7 that fallback elided all but the last 12 rounds at about 45K tokens of a 150K
/// budget, and the model spent its turn reading the same files again.
/// </summary>
public sealed class ReplFactoryInTurnLimitsTests
{
    [Fact]
    public void AKnownWindow_SetsBoth() =>
        Assert.Equal((400_000, 400_000), ReplFactory.InTurnLimits(400_000, "grok-4.7", contextBudget: null));

    [Fact]
    public void WithoutAWindow_BothFollowTheReplBudget_NotA200KFallback()
    {
        var budgetChars = TokenEstimator.EstimateChars(ModelContextWindow.GetBudget("grok-4.7"));

        Assert.Equal((budgetChars, budgetChars), ReplFactory.InTurnLimits(0, "grok-4.7", contextBudget: null));
        Assert.True(budgetChars > 200_000, $"{budgetChars} chars");
    }

    [Fact]
    public void ALoweredContextBudget_LowersThemToo()
    {
        var (maxInTurn, trigger) = ReplFactory.InTurnLimits(0, "llama3.1:8b", contextBudget: 16_000);

        Assert.Equal(TokenEstimator.EstimateChars(16_000), maxInTurn);
        Assert.Equal(maxInTurn, trigger);
    }

    [Fact]
    public void ReplLimits_CarryTheConfiguredBudget() =>
        Assert.Equal(16_000, ReplLimits.From(new ReplDefaultsConfig { ContextBudget = 16_000 }).ContextBudget);
}
