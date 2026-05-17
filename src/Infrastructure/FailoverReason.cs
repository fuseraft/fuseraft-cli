namespace fuseraft.Infrastructure;

/// <summary>
/// Classifies the reason a provider call failed in a way that may warrant trying a fallover model.
/// </summary>
public enum FailoverReason
{
    /// <summary>Error could not be mapped to a known provider failure type.</summary>
    None,

    /// <summary>HTTP 429 — request-rate limit exceeded (transient; retry later or switch provider).</summary>
    RateLimit,

    /// <summary>HTTP 400 — prompt exceeded the model's context window.</summary>
    ContextExceeded,

    /// <summary>HTTP 429 with quota or billing language — account-level limit exhausted.</summary>
    QuotaExceeded,

    /// <summary>HTTP 5xx — provider-side server error after all per-request retries are exhausted.</summary>
    ServerError,

    /// <summary>HTTP 401 or 403 — invalid or missing credentials. Not fallover-able by default.</summary>
    AuthError,
}
