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

    /// <summary>Start every session as if <c>--yolo</c> were passed: HITL off and no filesystem/shell/git sandbox.</summary>
    [JsonPropertyName("yolo")]
    public bool Yolo { get; set; }

    /// <summary>Optional plugins enabled by default, e.g. <c>["Scratchpad", "Http"]</c>. Merged with <c>--plugins</c>.</summary>
    [JsonPropertyName("plugins")]
    public List<string> Plugins { get; set; } = [];

    /// <summary>
    /// Auto-compact history when context crosses 75% of budget, instead of only warning.
    /// Default on; set false to restore the old warn-only behavior.
    /// </summary>
    [JsonPropertyName("autoCompact")]
    public bool AutoCompact { get; set; } = true;

    /// <summary>
    /// How many of a resumed session's most recent turns to re-display when it is restored
    /// (<c>--resume</c> or <c>/switch</c>). 0 disables the replay; <c>/replay</c> still works on demand.
    /// </summary>
    [JsonPropertyName("resumeReplayTurns")]
    public int ResumeReplayTurns { get; set; } = 3;

    /// <summary>
    /// Start every session in <c>/hitl auto</c>: with HITL on, shell commands that are provably
    /// read-only (<c>ls</c>, <c>git status</c>, <c>grep</c>, …) run without a y/N prompt, and
    /// everything that can change something still asks. Default off — every shell command asks.
    /// </summary>
    [JsonPropertyName("hitlAutoApproveReadOnly")]
    public bool HitlAutoApproveReadOnly { get; set; }

    /// <summary>
    /// Fraction of the context budget at which the REPL warns and (when <see cref="AutoCompact"/> is on)
    /// auto-compacts. Null uses 0.75.
    /// </summary>
    [JsonPropertyName("autoCompactThreshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AutoCompactThreshold { get; set; }

    /// <summary>Fraction of the context budget kept verbatim as recent turns when compacting. Null uses 0.20.</summary>
    [JsonPropertyName("compactPreserveTailRatio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CompactPreserveTailRatio { get; set; }

    /// <summary>Consecutive failing tool calls that end a turn. Null uses 3.</summary>
    [JsonPropertyName("maxConsecutiveToolFailures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxConsecutiveToolFailures { get; set; }

    /// <summary>Consecutive identical tool calls (same name and arguments) that end a turn. Null uses 5.</summary>
    [JsonPropertyName("maxIdenticalToolCalls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxIdenticalToolCalls { get; set; }

    /// <summary>Consecutive identical tool calls at which the model is nudged to change course; kept below <see cref="MaxIdenticalToolCalls"/>. Null uses 3.</summary>
    [JsonPropertyName("warnIdenticalToolCalls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WarnIdenticalToolCalls { get; set; }

    /// <summary>Automatic retries of a stream that dropped mid-response. Null uses 2.</summary>
    [JsonPropertyName("maxStreamRetries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxStreamRetries { get; set; }
}
