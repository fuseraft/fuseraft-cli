namespace fuseraft.Orchestration.Tracking;

/// <summary>In-memory snapshot of one completed function invocation.</summary>
public sealed record InvocationRecord(
    string Name,
    IReadOnlyDictionary<string, object?>? Args,
    bool Succeeded,
    string? Output = null);

/// <summary>In-memory snapshot of one search_symbol result, pending evidence-graph emission.</summary>
internal sealed record SymbolSearchRecord(string Symbol, string Output);

/// <summary>In-memory snapshot of one search_callers result, pending evidence-graph emission.</summary>
internal sealed record CallerSearchRecord(string Symbol, string Output);
