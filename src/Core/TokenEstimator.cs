namespace fuseraft.Core;

/// <summary>
/// Single chars-per-token heuristic for every pre-flight (pre-API-call) size estimate in the
/// codebase — contexts, tool schemas, and budgets that haven't been sent to a provider yet, so
/// no real token count exists. Once a turn completes, prefer the provider's own
/// <c>Usage.InputTokens</c> over any estimate here.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Default ratio for prose/mixed content: ~4 characters per token.</summary>
    public const int CharsPerToken = 4;

    /// <summary>
    /// Tighter ratio for code-heavy content (tool results, file reads), which tokenizes denser
    /// than prose — roughly 3 characters per token. Also used where the estimate needs to
    /// absorb overhead that isn't separately measured, such as tool-schema tokens.
    /// </summary>
    public const int CharsPerTokenDense = 3;

    /// <summary>Estimates the token count of <paramref name="chars"/> characters.</summary>
    public static int EstimateTokens(int chars, bool dense = false) =>
        chars / (dense ? CharsPerTokenDense : CharsPerToken);

    /// <summary>
    /// Inverse of <see cref="EstimateTokens"/>: the character budget equivalent to
    /// <paramref name="tokens"/> tokens.
    /// </summary>
    public static int EstimateChars(int tokens, bool dense = false) =>
        tokens * (dense ? CharsPerTokenDense : CharsPerToken);
}
