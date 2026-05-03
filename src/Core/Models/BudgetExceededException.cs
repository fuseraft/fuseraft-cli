namespace fuseraft.Core.Models;

/// <summary>
/// Thrown when the cumulative token count of a session exceeds the configured
/// <see cref="OrchestrationConfig.MaxTotalTokens"/> budget.
/// </summary>
public sealed class BudgetExceededException(int actual, int limit)
    : Exception($"Session token count {actual:N0} exceeded the configured budget of {limit:N0} tokens.")
{
    public int ActualTokens { get; } = actual;
    public int LimitTokens  { get; } = limit;
}
