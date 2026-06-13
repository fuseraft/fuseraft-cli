namespace fuseraft.Core.Models.Context;

/// <summary>
/// Token consumption and estimated cost for a single agent turn.
/// </summary>
public record TokenUsage(int InputTokens, int OutputTokens)
{
    public int TotalTokens => InputTokens + OutputTokens;
}
