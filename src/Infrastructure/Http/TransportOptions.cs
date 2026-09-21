using fuseraft.Core.Models.Config;

namespace fuseraft.Infrastructure;

/// <summary>Timeouts and retry budget for model HTTP calls from the global config's <c>provider</c> section, clamped so a hand-edited file can't wedge every call.</summary>
public sealed record TransportOptions
{
    public const int DefaultRequestTimeoutSeconds    = 20 * 60;
    public const int DefaultStreamIdleTimeoutSeconds = 5 * 60;
    public const int DefaultMaxRetries               = 3;

    public const int MinRequestTimeoutSeconds    = 30;
    public const int MaxRequestTimeoutSeconds    = 2 * 60 * 60;
    public const int MinStreamIdleTimeoutSeconds = 30;
    public const int MaxStreamIdleTimeoutSeconds = 60 * 60;
    public const int MaxRetriesLimit               = 10;

    // Dead-socket floor: with no bytes at all (not even keep-alive pings) for this long the connection is gone.
    private static readonly TimeSpan MinByteIdleTimeout = TimeSpan.FromSeconds(120);

    public static TransportOptions Default { get; } = new();

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds);

    /// <summary>True when the request timeout was set explicitly; Ollama keeps its own (shorter) default otherwise.</summary>
    public bool RequestTimeoutConfigured { get; init; }

    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromSeconds(DefaultStreamIdleTimeoutSeconds);

    public int MaxRetries { get; init; } = DefaultMaxRetries;

    /// <summary>True when the retry budget was set explicitly, which also switches off the OpenAI SDK's own stacked retries.</summary>
    public bool MaxRetriesConfigured { get; init; }

    /// <summary>Never shorter than <see cref="StreamIdleTimeout"/>, or the byte-level check would fire first and make a raised idle window meaningless.</summary>
    public TimeSpan ByteIdleTimeout => StreamIdleTimeout > MinByteIdleTimeout ? StreamIdleTimeout : MinByteIdleTimeout;

    public static TransportOptions From(UserConfig? cfg) => new()
    {
        RequestTimeout = cfg?.RequestTimeoutSeconds is { } r
            ? TimeSpan.FromSeconds(Math.Clamp(r, MinRequestTimeoutSeconds, MaxRequestTimeoutSeconds))
            : TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds),
        RequestTimeoutConfigured = cfg?.RequestTimeoutSeconds is not null,
        StreamIdleTimeout = cfg?.StreamIdleTimeoutSeconds is { } s
            ? TimeSpan.FromSeconds(Math.Clamp(s, MinStreamIdleTimeoutSeconds, MaxStreamIdleTimeoutSeconds))
            : TimeSpan.FromSeconds(DefaultStreamIdleTimeoutSeconds),
        MaxRetries = cfg?.MaxRetries is { } m ? Math.Clamp(m, 0, MaxRetriesLimit) : DefaultMaxRetries,
        MaxRetriesConfigured = cfg?.MaxRetries is not null,
    };
}
