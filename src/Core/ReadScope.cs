namespace fuseraft.Core;

/// <summary>
/// Gives a stretch of work its own record of which files it has read this turn, and its own read
/// budget. A subagent runs this way: it shares the parent's file tools, so without a scope its reads
/// land in the parent's per-turn record, and the parent's next read of the same file is answered
/// with "already read — content is in context" for content only the subagent ever saw.
///
/// <para>
/// Scoped to the calling async flow (an <see cref="AsyncLocal{T}"/>), like <see cref="ApprovalScope"/>:
/// work that <see cref="BeginInThisFlow"/> is called from, and anything it awaits, uses the new scope;
/// the caller that started it keeps its own record. Writes still clear a path from the turn's record
/// as well as the scope's, since they change what is on disk for everyone.
/// </para>
/// </summary>
public sealed class ReadScope
{
    private static readonly AsyncLocal<ReadScope?> CurrentScope = new();

    /// <summary>The scope the current async flow reads in, or null for the turn's own record.</summary>
    public static ReadScope? Current => CurrentScope.Value;

    /// <summary>For the rest of the calling async flow, track reads in a fresh, empty scope.</summary>
    public static void BeginInThisFlow() => CurrentScope.Value = new ReadScope();

    /// <summary>Paths read in full within this scope.</summary>
    public HashSet<string> Read { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths whose last read in this scope was answered with a cache hint rather than content.</summary>
    public HashSet<string> Hinted { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Characters of file content returned within this scope.</summary>
    public int BudgetUsed { get; set; }
}
