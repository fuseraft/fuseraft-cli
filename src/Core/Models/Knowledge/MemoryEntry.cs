namespace fuseraft.Core.Models.Knowledge;

public sealed record MemoryEntry
{
    public string Guid        { get; init; } = string.Empty;
    public string Name        { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Type        { get; init; } = "project";
    public string Body        { get; init; } = string.Empty;
    public string FilePath    { get; init; } = string.Empty;
}
