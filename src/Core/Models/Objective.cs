namespace fuseraft.Core.Models;

/// <summary>
/// A long-horizon objective tracked across sessions. Stub — Gap 7 will expand all fields.
/// </summary>
public sealed record Objective
{
    public string Id          { get; init; } = string.Empty;
    public string Title       { get; init; } = string.Empty;
    public string Status      { get; init; } = "Active";
}
