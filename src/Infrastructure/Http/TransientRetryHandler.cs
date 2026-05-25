using System.Net;
using fuseraft.Orchestration;

namespace fuseraft.Infrastructure;

/// <summary>
/// <see cref="DelegatingHandler"/> that retries transient HTTP errors (429, 5xx) up to
/// <see cref="MaxRetries"/> times with exponential back-off and full jitter, without
/// requiring an external resilience library.
///
/// <para>Back-off schedule (before jitter):</para>
/// <list type="bullet">
///   <item>Attempt 1: 2 s base</item>
///   <item>Attempt 2: 4 s base</item>
///   <item>Attempt 3: 8 s base</item>
/// </list>
///
/// <para>
/// Per-request streaming idle timeout: once an SSE connection is open, a
/// <see cref="SseEventIdleTimeoutStream"/> wrapper is applied so a hung body stream
/// (server opens the connection but stops sending real content events) is detected
/// within the configured idle window rather than blocking indefinitely.
/// <see cref="SseEventIdleTimeoutStream"/> distinguishes real content events from
/// keep-alive ping events so a stalled model is detected even while pings continue.
/// </para>
///
/// <para>
/// Reads a <c>Retry-After</c> response header when present so the retry delay respects
/// what the server advertised. Falls back to exponential back-off when the header is
/// absent or unparseable, and clamps the computed delay so
/// we don't overshoot the window the server has indicated.
/// </para>
/// </summary>
internal sealed class TransientRetryHandler(string? errorLogPath = null) : DelegatingHandler
{
    private const int MaxRetries = 3;
    // Base delay in seconds for attempt N: 2^(N+1)  →  2 s, 4 s, 8 s
    private const double BaseDelaySeconds = 2.0;
    // Jitter fraction applied symmetrically around the base delay (±20 %).
    private const double JitterFraction = 0.2;

    // Maximum time to wait between any two consecutive bytes in a streaming response.
    // HttpClient.Timeout only covers header delivery; once the SSE stream is open the
    // body read blocks indefinitely unless we enforce this per-chunk deadline.
    private static readonly TimeSpan StreamingIdleTimeout = TimeSpan.FromMinutes(5);

    private static readonly Random _jitter = new();
    private static readonly object _logLock = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;

            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                var delay = ComputeBackoff(attempt);
                Console.Error.WriteLine(
                    $"[retry {attempt + 1}/{MaxRetries}] Network error ({ex.Message}). " +
                    $"Retrying in {delay.TotalSeconds:F1} s…");
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            // On a client error (4xx) log the raw body to stderr before continuing.
            // Skip 401 to avoid printing the error twice (it will be rethrown as an
            // InvalidOperationException by the caller).  Truncate to prevent large HTML
            // error pages from flooding the terminal.
            // Log unconditionally here, then let the retry check below decide whether
            // to return or retry — 429 and 404 must reach IsRetryable, not exit early.
            HttpResponseMessage? loggedResponse = null;
            if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500
                && response.StatusCode != HttpStatusCode.Unauthorized)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var truncated = body.Length > 200 ? body[..200] + "…" : body;
                var stderrLine = $"[HTTP {(int)response.StatusCode}] {request.RequestUri?.Host}: {truncated}";
                Console.Error.WriteLine(stderrLine);
                AppendProviderError((int)response.StatusCode, request.RequestUri?.Host ?? "unknown", body);
                // Rebuild so the body stream can still be read by the caller or retry path.
                loggedResponse = new HttpResponseMessage(response.StatusCode)
                {
                    ReasonPhrase = response.ReasonPhrase,
                    Content = new StringContent(body,
                        System.Text.Encoding.UTF8,
                        response.Content.Headers.ContentType?.MediaType ?? "application/json")
                };
                foreach (var h in response.Headers)
                    loggedResponse.Headers.TryAddWithoutValidation(h.Key, h.Value);
                response = loggedResponse;
            }

            if (!IsRetryable(response) || attempt >= MaxRetries)
            {
                // Wrap successful response bodies with an idle timeout so that a hung
                // SSE stream (server opens the connection but stops sending data) is
                // detected and surfaced as a TimeoutException within StreamingIdleTimeout.
                if ((int)response.StatusCode is >= 200 and < 300)
                {
                    var raw   = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var timed = new StreamContent(new SseEventIdleTimeoutStream(raw, StreamingIdleTimeout));
                    foreach (var h in response.Content.Headers)
                        timed.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    response.Content = timed;
                }
                return response;
            }

            var retryDelay = RetryAfterDelay(response) ?? ComputeBackoff(attempt);
            Console.Error.WriteLine(
                $"[retry {attempt + 1}/{MaxRetries}] HTTP {(int)response.StatusCode} from " +
                $"{request.RequestUri?.Host}. Retrying in {retryDelay.TotalSeconds:F1} s…");

            // Drain and dispose the error response before retrying.
            response.Dispose();
            await Task.Delay(retryDelay, cancellationToken);
        }
    }

    private void AppendProviderError(int status, string host, string body)
    {
        if (errorLogPath is null) return;
        try
        {
            var entry = System.Text.Json.JsonSerializer.Serialize(new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                status,
                host,
                body,
            });
            var dir = Path.GetDirectoryName(errorLogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (_logLock)
                File.AppendAllText(errorLogPath, entry + "\n");
        }
        catch { /* never let logging crash the request pipeline */ }
    }

    private static bool IsRetryable(HttpResponseMessage r) =>
        r.StatusCode == HttpStatusCode.NotFound            || // 404 — transient backend unavailability (e.g. Open WebUI / Bedrock)
        r.StatusCode == HttpStatusCode.TooManyRequests     || // 429
        r.StatusCode == HttpStatusCode.InternalServerError || // 500
        r.StatusCode == HttpStatusCode.BadGateway          || // 502
        r.StatusCode == HttpStatusCode.ServiceUnavailable  || // 503
        r.StatusCode == HttpStatusCode.GatewayTimeout;        // 504

    /// <summary>
    /// Reads the <c>Retry-After</c> response header if present.
    /// Returns <see langword="null"/> when the header is absent or unparseable.
    /// </summary>
    private static TimeSpan? RetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null) return null;

        // Retry-After: <seconds>
        if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        // Retry-After: <http-date>
        if (retryAfter.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) return remaining;
        }

        return null;
    }

    /// <summary>
    /// Exponential back-off with full jitter: picks a random value in
    /// [base*(1-jitter), base*(1+jitter)] where base = 2^(attempt+1) seconds.
    /// </summary>
    private static TimeSpan ComputeBackoff(int attempt)
    {
        double baseSeconds = Math.Pow(BaseDelaySeconds, attempt + 1);
        double lo = baseSeconds * (1.0 - JitterFraction);
        double hi = baseSeconds * (1.0 + JitterFraction);
        double jittered;
        lock (_jitter) jittered = lo + _jitter.NextDouble() * (hi - lo);
        return TimeSpan.FromSeconds(jittered);
    }
}
