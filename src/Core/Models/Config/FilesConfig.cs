using System.Text.Json.Serialization;

namespace fuseraft.Core.Models.Config;

/// <summary>
/// Configures how the file tools keep track of the files the agent has seen (see
/// <c>fuseraft.Infrastructure.Context.FileLedger</c>), shared by the REPL and <c>fuseraft run</c>.
/// </summary>
public record FilesConfig
{
    public const int DefaultTrackedFiles = 50;
    public const int MaxTrackedFiles     = 5_000;

    /// <summary>
    /// How many recently read or written files are checked for changes before and after each tool call. Each check is a
    /// lookup per file, which adds up on a network share. 0 turns change tracking off. Null uses <see cref="DefaultTrackedFiles"/>.
    /// </summary>
    [JsonPropertyName("trackedFiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TrackedFiles { get; init; }
}
