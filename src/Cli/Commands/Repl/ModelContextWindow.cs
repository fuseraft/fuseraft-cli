namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Rough per-family working context-token budgets for REPL history trimming.
///
/// <para>
/// Not an authoritative model registry — a conservative heuristic so the REPL doesn't evict
/// history at a fixed ceiling regardless of what the connected model can actually hold.
/// Deliberately budgets well under each family's advertised maximum context window, since the
/// REPL's own token estimate (chars/4) is rough and doesn't account for tool-schema tokens,
/// which aren't part of the message-history estimate but do count against the same input limit.
/// </para>
/// </summary>
internal static class ModelContextWindow
{
    /// <summary>Fallback for unrecognized model IDs — local/Ollama models are typically
    /// configured with much smaller real context windows, so this is the safer assumption
    /// when the family can't be identified from the ID string.</summary>
    internal const int DefaultBudget = 80_000;

    // 128K+/1M-class frontier models.
    private const int LargeBudget = 150_000;

    // ~128K-class models not already covered by LargeFamilyMarkers.
    private const int MediumBudget = 100_000;

    private static readonly string[] LargeFamilyMarkers =
        ["claude", "gemini", "grok", "gpt-5", "gpt-4.1", "o1", "o3"];

    private static readonly string[] MediumFamilyMarkers =
        ["gpt-4o", "gpt-4", "mistral", "deepseek"];

    /// <summary>
    /// Returns the working token budget for <paramref name="modelId"/>, matched by substring
    /// so both bare model IDs (e.g. <c>claude-sonnet-4-6</c>) and provider-prefixed deployment
    /// IDs (e.g. Bedrock's <c>anthropic.claude-sonnet-4-6-20250929-v1:0</c>) resolve correctly.
    /// </summary>
    /// <param name="modelId">The model ID whose family determines the heuristic budget.</param>
    /// <param name="overrideBudget">
    /// User-configured override (<see cref="fuseraft.Core.Models.Config.UserConfig.ReplContextBudget"/>).
    /// When positive, takes precedence over the per-family heuristic below.
    /// </param>
    internal static int GetBudget(string? modelId, int? overrideBudget = null)
    {
        if (overrideBudget is > 0) return overrideBudget.Value;

        if (string.IsNullOrWhiteSpace(modelId)) return DefaultBudget;

        if (LargeFamilyMarkers.Any(m => modelId.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return LargeBudget;
        if (MediumFamilyMarkers.Any(m => modelId.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return MediumBudget;

        return DefaultBudget;
    }
}
