using System.ClientModel;
using System.Net.Http;

namespace fuseraft.Infrastructure;

/// <summary>
/// Inspects exceptions thrown by <see cref="Microsoft.Extensions.AI.IChatClient"/> implementations
/// and maps them to a <see cref="FailoverReason"/> so <see cref="FalloverChatClient"/> can decide
/// whether to try the next model in the chain.
/// </summary>
public static class ProviderErrorClassifier
{
    /// <summary>
    /// Default reasons that trigger a fallover attempt when <c>FalloverOn</c> is not configured.
    /// Covers rate limits, context overflow, quota exhaustion, and server errors.
    /// <see cref="FailoverReason.AuthError"/> is excluded — it signals a permanent configuration
    /// problem and should surface immediately rather than retrying on other models.
    /// </summary>
    public static readonly IReadOnlySet<FailoverReason> DefaultFalloverOn =
        new HashSet<FailoverReason>
        {
            FailoverReason.RateLimit,
            FailoverReason.ContextExceeded,
            FailoverReason.QuotaExceeded,
            FailoverReason.ServerError,
        };

    /// <summary>
    /// Parses a user-supplied list of reason strings into a set of <see cref="FailoverReason"/>
    /// values. Returns <see cref="DefaultFalloverOn"/> when <paramref name="values"/> is null.
    /// Unrecognized strings are silently ignored.
    /// </summary>
    public static IReadOnlySet<FailoverReason> ParseFalloverOn(IEnumerable<string>? values)
    {
        if (values is null) return DefaultFalloverOn;

        var set = new HashSet<FailoverReason>();
        foreach (var v in values)
        {
            if (Enum.TryParse<FailoverReason>(v, ignoreCase: true, out var r) && r != FailoverReason.None)
                set.Add(r);
        }
        return set;
    }

    /// <summary>
    /// Walks the exception chain and returns the most specific <see cref="FailoverReason"/>
    /// that can be inferred from HTTP status codes and message keywords.
    /// Returns <see cref="FailoverReason.None"/> when no classifiable provider signal is found.
    /// </summary>
    public static FailoverReason Classify(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var status = TryGetStatus(e);
            var msg    = e.Message;

            if (status is not null)
            {
                switch (status.Value)
                {
                    case 401: case 403:
                        return FailoverReason.AuthError;

                    case 429:
                        return IsQuotaMessage(msg) ? FailoverReason.QuotaExceeded : FailoverReason.RateLimit;

                    case 400 when IsContextExceededMessage(msg):
                    case 400 when IsThinkingTokenMismatch(msg):
                        return FailoverReason.ContextExceeded;

                    case 413:
                        return FailoverReason.ContextExceeded;

                    case >= 500:
                        return FailoverReason.ServerError;
                }
            }

            // String-based fallback for exceptions that don't expose a status code.
            // Checked in priority order: payload/context exceeded before rate-limit before auth before server.
            if (IsPayloadTooLargeMessage(msg)) return FailoverReason.ContextExceeded;
            if (IsThinkingTokenMismatch(msg))  return FailoverReason.ContextExceeded;
            if (IsContextExceededMessage(msg)) return FailoverReason.ContextExceeded;
            if (Is429Message(msg))             return IsQuotaMessage(msg) ? FailoverReason.QuotaExceeded : FailoverReason.RateLimit;
            if (IsAuthMessage(msg))            return FailoverReason.AuthError;
            if (IsServerErrorMessage(msg))     return FailoverReason.ServerError;
        }

        return FailoverReason.None;
    }

    // Extracts an HTTP status code from exception types that carry one.
    private static int? TryGetStatus(Exception e)
    {
        if (e is ClientResultException cre) return cre.Status;
        if (e is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
            return (int)httpEx.StatusCode.Value;
        return null;
    }

    private static bool IsContextExceededMessage(string msg) =>
        msg.Contains("context_length_exceeded",  StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("maximum context",           StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("too many tokens",           StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("reduce the length of",      StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Please reduce your prompt", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("prompt is too long",        StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("context window is full",    StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("token limit exceeded",      StringComparison.OrdinalIgnoreCase);

    private static bool Is429Message(string msg) =>
        msg.Contains("429",               StringComparison.Ordinal) ||
        msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("rate limit",        StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("rate_limit",        StringComparison.OrdinalIgnoreCase);

    private static bool IsQuotaMessage(string msg) =>
        msg.Contains("quota",              StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("billing",            StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("credits",            StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("spending limit",     StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("used all available", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthMessage(string msg) =>
        msg.Contains("Unauthorized",   StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Forbidden",      StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Invalid API key",StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("invalid_api_key",StringComparison.OrdinalIgnoreCase);

    private static bool IsServerErrorMessage(string msg) =>
        msg.Contains("Internal Server Error", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Bad Gateway",           StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Service Unavailable",   StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Gateway Timeout",       StringComparison.OrdinalIgnoreCase);

    // Bedrock/LiteLLM: "max_tokens must be greater than thinking.budget_tokens"
    // Fired when a thinking model's budget exceeds the configured MaxTokens.
    private static bool IsThinkingTokenMismatch(string msg) =>
        msg.Contains("budget_tokens", StringComparison.OrdinalIgnoreCase) ||
        (msg.Contains("max_tokens",  StringComparison.OrdinalIgnoreCase) &&
         msg.Contains("thinking",    StringComparison.OrdinalIgnoreCase) &&
         msg.Contains("greater",     StringComparison.OrdinalIgnoreCase));

    // nginx/proxy: "413 Request Entity Too Large" — payload exceeds proxy limit.
    private static bool IsPayloadTooLargeMessage(string msg) =>
        msg.Contains("Request Entity Too Large", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Payload Too Large",        StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("HTTP 413",                 StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("[413]",                    StringComparison.Ordinal);
}
