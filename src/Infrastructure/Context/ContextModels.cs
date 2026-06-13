using System.Text.Json.Serialization;

namespace fuseraft.Infrastructure.Context;

public sealed class ContextIndex
{
    [JsonPropertyName("items")]
    public Dictionary<string, ContextItem> Items { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ContextItem
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; init; } = string.Empty;

    [JsonPropertyName("importedAt")]
    public DateTime ImportedAt { get; init; }

    [JsonPropertyName("files")]
    public List<ContextFileEntry> Files { get; init; } = [];

    /// <summary>
    /// Set when one or more source files were binary documents that were converted to
    /// plain text at import time. Contains one note per extracted file.
    /// </summary>
    [JsonPropertyName("extractionInfo")]
    public string? ExtractionInfo { get; init; }
}

public sealed record ContextFileEntry(
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("sizeBytes")]    long   SizeBytes);
