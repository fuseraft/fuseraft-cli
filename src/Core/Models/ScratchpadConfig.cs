using fuseraft.Core;

namespace fuseraft.Core.Models;

/// <summary>
/// Configuration for the per-agent session-scoped scratchpad.
///
/// Agents opt in by adding <c>"Scratchpad"</c> to their <c>Plugins</c> list.
/// Each agent gets its own isolated file within the session directory; nothing
/// is shared unless an agent explicitly reads from the <c>global</c> scope.
/// </summary>
public record ScratchpadConfig
{
    /// <summary>
    /// Fallback base directory when no session ID is available.
    /// Supports <c>~</c> expansion. Defaults to <c>~/.fuseraft/scratchpad</c>.
    /// At runtime, <c>AgentFactory</c> overrides this with the session-scoped path
    /// (<c>~/.fuseraft/sessions/{project}/{session}/scratchpad</c>) when a session ID is set.
    /// </summary>
    public string BasePath { get; init; } = FuseraftPaths.GlobalScratchpad;
}
