using System.Globalization;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Core.Models.Config;
using fuseraft.Core.Subagents;
using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands;

/// <summary>The kind of value a setting takes, which decides how a client edits it.</summary>
public enum SettingKind { Text, Bool, Integer, Number, Choice, List }

/// <summary>One key <c>fuseraft settings set</c> accepts: what it takes, its default and how to read it from the config.</summary>
public sealed record SettingInfo(
    string Key, SettingKind Kind, string Description, Func<UserConfig, string?> Read,
    string? Default = null, double? Min = null, double? Max = null, IReadOnlyList<string>? Choices = null)
{
    /// <summary>Whether <c>""</c> clears the value back to its default; a true/false setting is always one or the other.</summary>
    public bool Clearable { get; init; } = Kind != SettingKind.Bool;
}

/// <summary>Every global setting, in the order <c>settings set</c> lists them and clients show them.</summary>
public static class SettingsCatalog
{
    public static IReadOnlyList<SettingInfo> All { get; } =
    [
        new("provider.modelId",      SettingKind.Text, "Model ID for chats, e.g. claude-sonnet-4-6.", c => Blank(c.ModelId)) { Clearable = false },
        new("provider.endpoint",     SettingKind.Text, "Provider base URL. Detected from the model ID when empty.", c => Blank(c.Endpoint)),
        new("provider.type",         SettingKind.Text, "Provider identifier, e.g. openai, anthropic or ollama. Detected when empty.", c => Blank(c.Provider)),
        new("provider.apiKeyEnvVar", SettingKind.Text, "Environment variable to read the API key from, instead of the keychain.", c => Blank(c.ApiKeyEnvVar)),
        new("provider.requestTimeoutSeconds", SettingKind.Integer,
            "Whole-request timeout for model calls, in seconds. Ollama keeps its own 100 s unless this is set.",
            c => Int(c.RequestTimeoutSeconds), Int(TransportOptions.DefaultRequestTimeoutSeconds),
            TransportOptions.MinRequestTimeoutSeconds, TransportOptions.MaxRequestTimeoutSeconds),
        new("provider.streamIdleTimeoutSeconds", SettingKind.Integer,
            "Seconds a streaming reply may go without content before it's treated as stalled.",
            c => Int(c.StreamIdleTimeoutSeconds), Int(TransportOptions.DefaultStreamIdleTimeoutSeconds),
            TransportOptions.MinStreamIdleTimeoutSeconds, TransportOptions.MaxStreamIdleTimeoutSeconds),
        new("provider.maxRetries", SettingKind.Integer,
            "Retries of a transient HTTP failure (429, 5xx or network) per model call. Setting it also turns off the SDK's own retries, so 0 means one attempt.",
            c => Int(c.MaxRetries), Int(TransportOptions.DefaultMaxRetries), 0, TransportOptions.MaxRetriesLimit),

        new("sampling.temperature",     SettingKind.Number,  "Sampling temperature. Unset uses the provider's default.", c => Num(c.Sampling.Temperature), Min: 0, Max: 2),
        new("sampling.topP",            SettingKind.Number,  "Nucleus sampling cutoff. Unset uses the provider's default.", c => Num(c.Sampling.TopP), Min: 0, Max: 1),
        new("sampling.seed",            SettingKind.Integer, "Sampling seed, for providers that support one.", c => Long(c.Sampling.Seed)),
        new("sampling.maxOutputTokens", SettingKind.Integer, "Most tokens one reply may produce. Unset uses the provider's default.", c => Int(c.Sampling.MaxOutputTokens), Min: 1),

        new("repl.contextBudget", SettingKind.Integer, "Working context budget in tokens, overriding the built-in estimate for the model's family.", c => Int(c.Repl.ContextBudget), Min: 1),
        new("repl.theme", SettingKind.Text, "Full-screen terminal REPL color theme: default, bright, light, mono, rust, or a file in ~/.fuseraft/themes.", c => c.Repl.Theme, "default"),
        new("repl.dock",  SettingKind.Choice, "Where the full-screen terminal REPL puts the prompt.", c => c.Repl.Dock, "bottom", Choices: ["bottom", "top"]),
        new("repl.bell",  SettingKind.Bool, "Ring the terminal bell when a turn ends or a question waits while you're away.", c => Bool(c.Repl.Bell), "true"),
        new("repl.autoTitle", SettingKind.Bool, "Name each chat from its first exchange, using memory.model if set.", c => Bool(c.Repl.AutoTitle), "true"),
        new("repl.noBanner",  SettingKind.Bool, "Start the terminal REPL without its banner.", c => Bool(c.Repl.NoBanner), "false"),
        new("repl.verbose",   SettingKind.Bool, "Show debug-level log output.", c => Bool(c.Repl.Verbose), "false"),
        new("repl.safeMode",  SettingKind.Bool, "Start every chat in /safe-mode.", c => Bool(c.Repl.SafeModeDefault), "false"),
        new("repl.yolo",      SettingKind.Bool, "Start every chat as if --yolo were passed: no approval prompts and no sandbox.", c => Bool(c.Repl.Yolo), "false"),
        new("repl.autoCompact", SettingKind.Bool, "Compact the conversation automatically at the threshold, instead of only warning.", c => Bool(c.Repl.AutoCompact), "true"),
        new("repl.resumeReplayTurns", SettingKind.Integer, "Recent turns re-displayed when a chat is resumed. 0 turns this off.",
            c => Int(c.Repl.ResumeReplayTurns), "3", 0) { Clearable = false },
        new("repl.hitlAutoApproveReadOnly", SettingKind.Bool, "With approvals on, skip the prompt for shell commands that are provably read-only.",
            c => Bool(c.Repl.HitlAutoApproveReadOnly), "false"),
        new("repl.autoCompactThreshold", SettingKind.Number, "Fraction of the context window at which the chat warns or compacts.",
            c => Num(c.Repl.AutoCompactThreshold), Num(ReplLimits.DefaultAutoCompactThreshold), ReplLimits.AutoCompactThresholdMin, ReplLimits.AutoCompactThresholdMax),
        new("repl.compactPreserveTailRatio", SettingKind.Number, "Fraction of the context window kept word for word as recent turns when compacting.",
            c => Num(c.Repl.CompactPreserveTailRatio), Num(ReplLimits.DefaultPreserveTailRatio), ReplLimits.PreserveTailRatioMin, ReplLimits.PreserveTailRatioMax),
        new("repl.maxConsecutiveToolFailures", SettingKind.Integer, "Rounds of tool calls in a row in which every call failed before a turn stops, or asks whether to keep going.",
            c => Int(c.Repl.MaxConsecutiveToolFailures), Int(ReplTurn.MaxConsecutiveToolFailures), ReplLimits.ToolFailuresMin, ReplLimits.ToolFailuresMax),
        new("repl.maxIdenticalToolCalls", SettingKind.Integer, "Identical tool calls in a row that end a turn.",
            c => Int(c.Repl.MaxIdenticalToolCalls), Int(ReplTurn.MaxConsecutiveIdenticalToolCalls), ReplLimits.IdenticalCallsMin, ReplLimits.IdenticalCallsMax),
        new("repl.warnIdenticalToolCalls", SettingKind.Integer, "Identical tool calls in a row at which the model is nudged. Must be below repl.maxIdenticalToolCalls.",
            c => Int(c.Repl.WarnIdenticalToolCalls), Int(ReplTurn.SoftRepeatedToolCallThreshold), ReplLimits.WarnIdenticalCallsMin, ReplLimits.IdenticalCallsMax - 1),
        new("repl.maxStreamRetries", SettingKind.Integer, "Automatic retries of a reply stream that dropped partway through.",
            c => Int(c.Repl.MaxStreamRetries), Int(ReplLimits.DefaultMaxStreamRetries), 0, ReplLimits.StreamRetriesMax),
        new("repl.maxTodoNudges", SettingKind.Integer, "Follow-up turns sent when the model stops with its own todo items still open. 0 turns them off.",
            c => Int(c.Repl.MaxTodoNudges), Int(ReplTurn.MaxTodoCorrectionRounds), 0, ReplLimits.TodoNudgesMax),
        new("repl.maxTurnInputTokens", SettingKind.Integer, "Uncached input tokens one turn may spend across all its model calls before it stops.",
            c => Int(c.Repl.MaxTurnInputTokens), Int(ReplLimits.DefaultMaxTurnInputTokens), ReplLimits.TurnInputTokensMin, ReplLimits.TurnInputTokensMax),
        new("repl.plugins", SettingKind.List, "Extra plugins every chat starts with, comma-separated, e.g. Scratchpad,Http.",
            c => c.Repl.Plugins.Count > 0 ? string.Join(",", c.Repl.Plugins) : null),

        new("telemetry.otlpEndpoint", SettingKind.Text, "OTLP endpoint to send traces to. Empty turns telemetry off.", c => c.Telemetry?.OtlpEndpoint),
        new("telemetry.serviceName",  SettingKind.Text, "Service name on exported traces. Needs telemetry.otlpEndpoint.", c => c.Telemetry?.ServiceName),

        new("skillCuration.enabled", SettingKind.Bool, "After a successful orchestration session, save reusable procedures from it as skills.", c => Bool(c.SkillCuration?.Enabled), "false"),
        new("skills.searchDepth", SettingKind.Integer, "Directory levels scanned inside each skill for resources and scripts.",
            c => Int(c.Skills?.SearchDepth), Int(SkillsConfig.DefaultSearchDepth), 1, SkillsConfig.MaxSearchDepth),

        new("memory.autoSave", SettingKind.Bool, "Save memories automatically on exit and before /clear, /switch or compaction. /memory save always works.",
            c => Bool(c.Memory?.AutoSave), "false"),
        new("memory.model",        SettingKind.Text, "Model for saving memories and titling chats, e.g. a cheaper one. Empty uses the chat model.", c => c.Memory?.Model),
        new("memory.provider",     SettingKind.Text, "Provider for memory.model. Detected when empty.", c => c.Memory?.Provider),
        new("memory.endpoint",     SettingKind.Text, "Base URL for memory.model. It reuses the main provider's API key unless memory.apiKeyEnvVar is set.", c => c.Memory?.Endpoint),
        new("memory.apiKeyEnvVar", SettingKind.Text, "Environment variable holding the API key for memory.model.", c => c.Memory?.ApiKeyEnvVar),

        new("subagent.model",        SettingKind.Text, "Model for /explore, /locate and /delegate, e.g. a cheaper one. Empty uses the chat model.", c => c.Subagent?.Model),
        new("subagent.provider",     SettingKind.Text, "Provider for subagent.model. Detected when empty.", c => c.Subagent?.Provider),
        new("subagent.endpoint",     SettingKind.Text, "Base URL for subagent.model. It reuses the main provider's API key unless subagent.apiKeyEnvVar is set.", c => c.Subagent?.Endpoint),
        new("subagent.apiKeyEnvVar", SettingKind.Text, "Environment variable holding the API key for subagent.model.", c => c.Subagent?.ApiKeyEnvVar),
        new("subagent.exploreMaxIterations",  SettingKind.Integer, "Round cap for /explore.",  c => Int(c.Subagent?.ExploreMaxIterations),  "20", 1, SubagentDefinitionLoader.MaxIterationsCeiling),
        new("subagent.delegateMaxIterations", SettingKind.Integer, "Round cap for /delegate.", c => Int(c.Subagent?.DelegateMaxIterations), "40", 1, SubagentDefinitionLoader.MaxIterationsCeiling),
        new("subagent.exploreTimeoutMinutes",  SettingKind.Integer, "Time limit for /explore, in minutes.",  c => Int(c.Subagent?.ExploreTimeoutMinutes),  "8",  1, SubagentConfig.MaxTimeoutMinutes),
        new("subagent.locateTimeoutMinutes",   SettingKind.Integer, "Time limit for /locate, in minutes.",   c => Int(c.Subagent?.LocateTimeoutMinutes),   "2",  1, SubagentConfig.MaxTimeoutMinutes),
        new("subagent.delegateTimeoutMinutes", SettingKind.Integer, "Time limit for /delegate, in minutes.", c => Int(c.Subagent?.DelegateTimeoutMinutes), "15", 1, SubagentConfig.MaxTimeoutMinutes),
    ];

    public static SettingInfo? Find(string key) =>
        All.FirstOrDefault(s => s.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));

    static string? Blank(string? v) => string.IsNullOrEmpty(v) ? null : v;
    static string? Int(int? v) => v?.ToString(CultureInfo.InvariantCulture);
    static string? Long(long? v) => v?.ToString(CultureInfo.InvariantCulture);
    static string? Num(double? v) => v?.ToString(CultureInfo.InvariantCulture);
    static string? Bool(bool? v) => v is { } b ? (b ? "true" : "false") : null;
}
