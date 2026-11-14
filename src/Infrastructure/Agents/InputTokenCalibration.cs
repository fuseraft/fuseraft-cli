using fuseraft.Core;

namespace fuseraft.Infrastructure.Agents;

/// <summary>
/// How much bigger the provider's own count of the last request was than fuseraft's character estimate of it,
/// so in-turn budgets shrink to match what the provider really counts.
/// </summary>
/// <remarks>
/// The estimate can't see everything a provider bills. xAI adds back the hidden reasoning of earlier assistant
/// rounds (matched by their tool-call IDs) and counts it as prompt: late in a grok-4.7 turn that was about 30% of the
/// request, and billed tokens ran 1.5–2x the estimate. Like Cline's underestimate factor, this only ever tightens a
/// budget, and is capped so one odd count can't collapse it.
///
/// The factor only rises. xAI adds the reasoning back only while the history is unchanged, so a request that was
/// trimmed counts lower (1.25x against 1.75x untrimmed in a live run). Letting that lower reading loosen the budget
/// made requests alternate between trimmed and untrimmed, missing the prompt cache on every switch: 1M uncached tokens
/// in 90 calls. Small requests are skipped too, since the provider's fixed overhead per request inflates their ratio.
/// </remarks>
internal sealed class InputTokenCalibration(int minSampleChars = 0)
{
    internal const double MaxFactor = 4;

    private double _factor = 1;

    /// <summary>The most provider tokens per estimated token seen on any request big enough to judge; at least 1.</summary>
    internal double Factor => Volatile.Read(ref _factor);

    /// <summary>Records what the provider counted for a request fuseraft estimated at <paramref name="estimatedChars"/>.</summary>
    internal void Record(int estimatedChars, long promptTokens)
    {
        if (promptTokens <= 0 || estimatedChars <= 0 || estimatedChars < minSampleChars) return;
        var ratio = promptTokens / ((double)estimatedChars / TokenEstimator.CharsPerToken);
        // Quarter steps, so a budget holds still from one round to the next and the trim cut stays put for the prompt cache.
        var factor = Math.Clamp(Math.Ceiling(ratio * 4) / 4, 1, MaxFactor);
        double seen;
        while (factor > (seen = Volatile.Read(ref _factor))
               && Interlocked.CompareExchange(ref _factor, factor, seen) != seen) { }
    }

    /// <summary><paramref name="chars"/> of budget in estimate units; 0 (no budget) stays 0.</summary>
    internal int Scale(int chars) => chars <= 0 ? chars : (int)(chars / Factor);
}
