namespace fuseraft.Core.Models.Config;

/// <summary>
/// Configures the model used for the REPL's memory-extraction LLM call — the automatic
/// end-of-session save (see <c>ReplTurn.ExtractMemoriesOnExitAsync</c>) and the manual
/// <c>/memory save</c> command. See <c>fuseraft.Infrastructure.Memory.MemoryExtractor</c>.
/// </summary>
public record MemoryExtractionConfig
{
    /// <summary>
    /// Model alias or ID to use for extraction (e.g. a small/cheap model to keep the
    /// end-of-session extraction call inexpensive). Resolved the same way as any other
    /// <see cref="ModelConfig.ModelId"/> — provider, endpoint, and API key are
    /// auto-detected from the prefix, independent of the REPL's main provider settings.
    /// Defaults to the REPL's main chat model when null or empty.
    /// </summary>
    public string? Model { get; init; }
}
