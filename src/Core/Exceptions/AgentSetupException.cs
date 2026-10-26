namespace fuseraft.Core.Exceptions;

/// <summary>
/// Thrown when an agent can't be built from its config because of the user's setup — e.g. a
/// remote agent's card can't be fetched — rather than a fuseraft bug, so no crash dump is written.
/// </summary>
public sealed class AgentSetupException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    /// <summary>The setup error anywhere in <paramref name="ex"/>'s chain; orchestrators that run agents inside a workflow can wrap it.</summary>
    public static AgentSetupException? FindIn(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is AgentSetupException setup) return setup;
            if (e is AggregateException agg)
                return agg.InnerExceptions.Select(FindIn).FirstOrDefault(s => s is not null);
        }
        return null;
    }
}
