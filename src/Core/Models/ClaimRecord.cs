namespace fuseraft.Core.Models;

/// <summary>
/// A verifiable claim with supporting evidence. Stub — Gap 3 will expand all fields.
/// </summary>
public sealed record ClaimRecord
{
    public string Id      { get; init; } = string.Empty;
    public string Claim   { get; init; } = string.Empty;
    public string Status  { get; init; } = "Assumed";
}
