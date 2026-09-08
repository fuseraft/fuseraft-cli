using System.Text.Json.Serialization;

namespace fuseraft.Core.Models.Config;

/// <summary>
/// Persisted REPL startup defaults. Each corresponds to a CLI flag on <c>fuseraft repl</c>
/// (<c>--no-banner</c>, <c>--verbose</c>, <c>--plugins</c>) or a runtime-only toggle
/// (<c>/safe-mode</c>) that previously had to be re-specified on every invocation. A flag
/// passed on the command line always takes effect in addition to (never instead of) the
/// configured default — see <c>ReplCommand.ExecuteAsync</c>.
/// </summary>
public sealed class ReplDefaultsConfig
{
    /// <summary>
    /// Overrides the REPL's heuristic working-context-token budget
    /// (<see cref="fuseraft.Cli.Commands.Repl.ModelContextWindow"/>). Null falls back to the
    /// built-in per-model-family heuristic.
    /// </summary>
    [JsonPropertyName("contextBudget")]
    public int? ContextBudget { get; set; }

    [JsonPropertyName("noBanner")]
    public bool NoBanner { get; set; }

    [JsonPropertyName("verbose")]
    public bool Verbose { get; set; }

    /// <summary>Engages <c>/safe-mode on</c> (blocks Shell/Git/Http tools) at session startup.</summary>
    [JsonPropertyName("safeMode")]
    public bool SafeModeDefault { get; set; }

    /// <summary>Optional plugins enabled by default, e.g. <c>["Scratchpad", "Http"]</c>. Merged with <c>--plugins</c>.</summary>
    [JsonPropertyName("plugins")]
    public List<string> Plugins { get; set; } = [];
}
