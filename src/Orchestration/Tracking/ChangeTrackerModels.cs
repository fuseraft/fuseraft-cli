namespace fuseraft.Orchestration.Tracking;

/// <summary>
/// In-memory snapshot of one completed function invocation. <see cref="Agent"/> and
/// <see cref="TurnIndex"/> are captured at the moment the call actually happened — not
/// inferred later from whichever turn's flush happens to drain it off the queue — so a
/// record left pending across a skipped flush keeps its true attribution.
/// </summary>
public sealed record InvocationRecord(
    string Name,
    IReadOnlyDictionary<string, object?>? Args,
    bool Succeeded,
    string? Output = null,
    string Agent = "",
    int TurnIndex = -1);

/// <summary>In-memory snapshot of one search_symbol result, pending evidence-graph emission.</summary>
internal sealed record SymbolSearchRecord(string Symbol, string Output);

/// <summary>In-memory snapshot of one search_callers result, pending evidence-graph emission.</summary>
internal sealed record CallerSearchRecord(string Symbol, string Output);
