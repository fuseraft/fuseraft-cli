namespace fuseraft.Core.Models;

/// <summary>
/// Configuration for the per-agent persistent scratchpad.
///
/// Agents opt in by adding <c>"Scratchpad"</c> to their <c>Plugins</c> list.
/// Each agent gets its own isolated file; nothing is shared unless an agent
/// explicitly reads from the <c>global</c> scope.
/// </summary>
public record ScratchpadConfig
{
    /// <summary>
    /// Directory where scratchpad files are stored.
    /// Supports <c>~</c> expansion. Defaults to <c>~/.fuseraft/scratchpad</c>.
    /// </summary>
    public string BasePath { get; init; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".fuseraft", "scratchpad");
}
