namespace fuseraft.Core.Models;

/// <summary>
/// Tracks the token budget available to the context assembly pipeline.
/// All units are estimated tokens (characters / 4).
/// </summary>
public sealed record TokenBudget(int TotalBudget, int Used, int Remaining)
{
    /// <summary>Returns true when <paramref name="chars"/> characters fit within the remaining budget.</summary>
    public bool Fits(int chars) => Remaining <= 0 || chars / 4 <= Remaining;

    /// <summary>Unlimited budget sentinel — use when no token limit is configured.</summary>
    public static readonly TokenBudget Unlimited = new(0, 0, 0);
}
