namespace fuseraft.Core.Models;

public sealed record AdrEntry
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Status { get; init; } = "Proposed";
    public string Date { get; init; } = string.Empty;
    public string Context { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public List<string> Alternatives { get; init; } = [];
    public List<string> Consequences { get; init; } = [];
    public List<string> Supersedes { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    /// <summary>File paths or SymbolId strings this decision governs; used to build adr_governs edges in the repository graph.</summary>
    public List<string> Governs { get; init; } = [];
}
