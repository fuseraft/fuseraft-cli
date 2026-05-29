using fuseraft.Core;

namespace fuseraft.Core.Models;

/// <summary>
/// Configuration for automatic change tracking.
///
/// When present in the orchestration config, a <c>ChangeTracker</c> is attached to every
/// agent's kernel as an <c>IFunctionInvocationFilter</c>. After each agent text turn the
/// tracker flushes recorded tool-call results to <see cref="Path"/> as a structured JSON log.
///
/// Agents can read the log via the <c>Changes</c> plugin to observe exactly what prior
/// agents did without inferring it from the chat history.
/// </summary>
public record ChangeTrackingConfig
{
    /// <summary>
    /// Path to write the change log JSON file.
    /// Relative paths are resolved against the current working directory.
    /// Defaults to <c>.fuseraft/state/changes.json</c>.
    /// </summary>
    public string Path { get; init; } = FuseraftPaths.LocalChanges;

    /// <summary>
    /// Path to write the intent log JSON file.
    /// When null, the path is derived from <see cref="Path"/> by replacing the filename
    /// with <c>intents.json</c> in the same directory.
    /// The intent log records tool calls before execution (PENDING) and updates them
    /// to APPLIED or FAILED after, enabling deterministic replay-based recovery.
    /// </summary>
    public string? IntentLogPath { get; init; }

    /// <summary>
    /// Resolves the effective intent log path. When <see cref="IntentLogPath"/> is null,
    /// derives the path from <see cref="Path"/> (same directory, filename <c>intents.json</c>).
    /// </summary>
    public string ResolveIntentLogPath()
    {
        if (IntentLogPath is { Length: > 0 }) return IntentLogPath;
        var dir = System.IO.Path.GetDirectoryName(Path) ?? FuseraftPaths.LocalState;
        return System.IO.Path.Combine(dir, "intents.json");
    }
}
