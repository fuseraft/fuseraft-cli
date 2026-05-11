using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using fuseraft.Core.Models;
using fuseraft.Orchestration;

namespace fuseraft.Infrastructure;

/// <summary>
/// Creates configured <see cref="IChatClient"/> instances from <see cref="ModelConfig"/>.
///
/// <para>
/// When <see cref="ModelConfig.Provider"/>, <see cref="ModelConfig.Endpoint"/>, or
/// <see cref="ModelConfig.ApiKeyEnvVar"/> are left empty, <see cref="Resolve"/> fills
/// them in by (1) checking the named model registry supplied at construction time, then
/// (2) pattern-matching <see cref="ModelConfig.ModelId"/> against known provider prefixes.
/// </para>
///
/// <para>Supported providers:</para>
/// <list type="bullet">
///   <item><b>openai</b> — OpenAI and any OpenAI-compatible API (xAI, Anthropic, DeepSeek, OpenRouter, …)</item>
///   <item><b>azure</b> — Azure OpenAI Service</item>
///   <item><b>google</b> — Google AI Gemini (via OpenAI-compatible endpoint)</item>
///   <item><b>mistral</b> — Mistral AI (via OpenAI-compatible endpoint)</item>
///   <item><b>ollama</b> — local Ollama server (no API key required)</item>
/// </list>
///
/// <para>
/// All chat clients share a single <see cref="HttpClient"/> backed by
/// <see cref="TransientRetryHandler"/> so transient API errors (429, 503, 504) are
/// retried with exponential back-off before surfacing to the orchestration layer.
/// </para>
/// </summary>
public sealed class ChatClientFactory(
    IReadOnlyDictionary<string, ModelConfig>? models = null,
    string? errorLogPath = null,
    EventEmitter? eventEmitter = null) : IDisposable
{
    // One shared HttpClient per factory instance (one per session). The retry handler
    // wraps SocketsHttpHandler for proper connection pooling.
    private readonly HttpClient _httpClient = BuildResilientClient(errorLogPath, eventEmitter);

    public void Dispose() => _httpClient.Dispose();

    // Provider presets:
    // Each entry maps a model-ID prefix (lower-cased) to the defaults used when the
    // caller has not explicitly specified Provider / Endpoint / ApiKeyEnvVar.

    private readonly record struct ProviderPreset(
        string Provider, string Endpoint, string ApiKeyEnvVar);

    // Checked in order — put more-specific prefixes first.
    private static readonly (string Prefix, ProviderPreset Defaults)[] ModelPrefixes =
    [
        ("gpt-",        new("openai",  "https://api.openai.com/v1",                           "OPENAI_API_KEY")),
        ("o1",          new("openai",  "https://api.openai.com/v1",                           "OPENAI_API_KEY")),
        ("o3",          new("openai",  "https://api.openai.com/v1",                           "OPENAI_API_KEY")),
        ("o4",          new("openai",  "https://api.openai.com/v1",                           "OPENAI_API_KEY")),
        ("grok-",       new("openai",  "https://api.x.ai/v1",                                 "XAI_API_KEY")),
        ("claude-",     new("openai",  "https://api.anthropic.com/v1",                        "ANTHROPIC_API_KEY")),
        ("gemini-",     new("google",  "https://generativelanguage.googleapis.com/v1beta/openai", "GOOGLE_AI_API_KEY")),
        ("learnlm-",    new("google",  "https://generativelanguage.googleapis.com/v1beta/openai", "GOOGLE_AI_API_KEY")),
        ("mistral-",    new("mistral", "https://api.mistral.ai/v1",                           "MISTRAL_API_KEY")),
        ("mixtral-",    new("mistral", "https://api.mistral.ai/v1",                           "MISTRAL_API_KEY")),
        ("codestral-",  new("mistral", "https://api.mistral.ai/v1",                           "MISTRAL_API_KEY")),
        ("pixtral-",    new("mistral", "https://api.mistral.ai/v1",                           "MISTRAL_API_KEY")),
        ("deepseek-",   new("openai",  "https://api.deepseek.com/v1",                         "DEEPSEEK_API_KEY")),
        ("llama",       new("ollama",  "http://localhost:11434",                               "")),
        ("phi",         new("ollama",  "http://localhost:11434",                               "")),
        ("qwen",        new("ollama",  "http://localhost:11434",                               "")),
        ("gemma",       new("ollama",  "http://localhost:11434",                               "")),
        ("codellama",   new("ollama",  "http://localhost:11434",                               "")),
        ("smollm",      new("ollama",  "http://localhost:11434",                               "")),
    ];

    // Public API

    /// <summary>
    /// Returns a fully-resolved copy of <paramref name="config"/> with all empty fields
    /// filled in from the model registry or provider auto-detection.
    /// </summary>
    public ModelConfig Resolve(ModelConfig config)
    {
        // 1. Registry lookup — replace with alias config, then fall through so any
        //    fields still empty on the alias get filled in by auto-detection below.
        if (models?.TryGetValue(config.ModelId, out var alias) == true)
        {
            var registryKey = config.ModelId;
            // Per-agent Temperature / MaxTokens always override the alias values.
            config = alias with
            {
                Temperature = config.Temperature ?? alias.Temperature,
                MaxTokens   = config.MaxTokens > 0 ? config.MaxTokens : alias.MaxTokens
            };
            // When an alias omits ModelId the user intends the registry key itself
            // to be the model name sent to the provider (e.g. a custom server that
            // knows the model by the same string the user uses in their config).
            if (string.IsNullOrEmpty(config.ModelId))
                config = config with { ModelId = registryKey };
        }

        // 2. Short-circuit — all connection fields already set.
        if (!string.IsNullOrEmpty(config.Provider)
            && !string.IsNullOrEmpty(config.Endpoint)
            && (!string.IsNullOrEmpty(config.ApiKeyEnvVar) || !string.IsNullOrEmpty(config.ApiKey)))
            return config;

        // 2b. Explicit endpoint + any form of auth (literal key or env-var reference).
        // Skip auto-detection and treat as OpenAI-compatible — the user supplied all necessary
        // connection info and auto-detection would only misidentify unusual model ID formats
        // (e.g. AWS Bedrock "anthropic.claude-...:0" being wrongly treated as an Ollama tag).
        if (!string.IsNullOrEmpty(config.Endpoint)
            && (!string.IsNullOrEmpty(config.ApiKey) || !string.IsNullOrEmpty(config.ApiKeyEnvVar)))
            return config with { Provider = string.IsNullOrEmpty(config.Provider) ? "openai" : config.Provider };

        // Ollama tag format: "modelname:tag" where the tag contains at least one letter
        // (e.g. "llama3:latest", "phi3:3.8b"). Purely numeric suffixes like ":0" or ":1"
        // are AWS Bedrock version specifiers, not Ollama tags.
        bool isOllamaTag = config.ModelId.Contains(':')
                           && !config.ModelId.Contains("://")
                           && HasOllamaStyleTag(config.ModelId);

        ProviderPreset? detected = isOllamaTag
            ? new ProviderPreset("ollama", "http://localhost:11434", "")
            : DetectFromPrefix(config.ModelId);

        if (detected is null)
        {
            // A custom Endpoint is an unambiguous signal that the caller knows which
            // provider to use — treat as OpenAI-compatible and skip the prefix check.
            // This covers non-standard model IDs (e.g. AWS Bedrock "anthropic.claude-...:0",
            // Open WebUI deployments) where the endpoint is set via global config or inline.
            if (!string.IsNullOrEmpty(config.Endpoint))
                return config with { Provider = string.IsNullOrEmpty(config.Provider) ? "openai" : config.Provider };

            // No endpoint and no detectable prefix — fail fast with a helpful message
            // rather than a cryptic missing-env-var error later.
            if (string.IsNullOrEmpty(config.Provider))
                throw new InvalidOperationException(
                    $"Cannot determine the LLM provider for model '{config.ModelId}'. " +
                    $"Specify 'Provider', 'Endpoint', and 'ApiKeyEnvVar' explicitly, " +
                    $"or add the model to the 'Models' registry in orchestration.yaml.");

            return config;
        }

        var preset = detected.Value;
        return config with
        {
            Provider    = string.IsNullOrEmpty(config.Provider)    ? preset.Provider    : config.Provider,
            Endpoint    = string.IsNullOrEmpty(config.Endpoint)    ? preset.Endpoint    : config.Endpoint,
            ApiKeyEnvVar = string.IsNullOrEmpty(config.ApiKeyEnvVar) ? preset.ApiKeyEnvVar : config.ApiKeyEnvVar,
        };
    }

    /// <summary>
    /// Builds an <see cref="IChatClient"/> from the supplied model config.
    /// Any empty fields are resolved first via <see cref="Resolve"/>.
    /// </summary>
    public IChatClient Create(ModelConfig config)
    {
        config = Resolve(config);

        // API key — optional for Ollama. Literal ApiKey takes precedence over env-var lookup.
        var apiKey = !string.IsNullOrEmpty(config.ApiKey)
            ? config.ApiKey
            : string.IsNullOrEmpty(config.ApiKeyEnvVar)
                ? string.Empty
                : Environment.GetEnvironmentVariable(config.ApiKeyEnvVar)
                  ?? throw new InvalidOperationException(
                      $"API key environment variable '{config.ApiKeyEnvVar}' is not set " +
                      $"(model: '{config.ModelId}', provider: '{config.Provider}').");

        var provider = config.Provider.Trim().ToLowerInvariant();
        var transport = new HttpClientPipelineTransport(_httpClient);

        switch (provider)
        {
            case "azure":
                if (string.IsNullOrEmpty(config.Endpoint))
                    throw new InvalidOperationException(
                        $"Provider 'azure' requires Endpoint to be set (deployment: '{config.ModelId}').");
                return new AzureOpenAIClient(
                    new Uri(config.Endpoint),
                    new ApiKeyCredential(apiKey),
                    new AzureOpenAIClientOptions { Transport = transport, NetworkTimeout = HttpClientTimeout })
                    .GetChatClient(config.ModelId)
                    .AsIChatClient();

            case "ollama":
                return new OllamaApiClient(
                    string.IsNullOrEmpty(config.Endpoint)
                        ? new Uri("http://localhost:11434")
                        : new Uri(config.Endpoint),
                    config.ModelId);

            default: // "openai", "google", "mistral" + every other OpenAI-compatible endpoint
                if (string.IsNullOrEmpty(config.Endpoint))
                    throw new InvalidOperationException(
                        $"Provider '{provider}' requires Endpoint to be set (model: '{config.ModelId}'). " +
                        $"This should have been filled in by auto-detection — check the model ID prefix.");
                return new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Transport = transport, Endpoint = new Uri(config.Endpoint), NetworkTimeout = HttpClientTimeout })
                    .GetChatClient(config.ModelId)
                    .AsIChatClient();
        }
    }

    // Helpers

    private static ProviderPreset? DetectFromPrefix(string modelId)
    {
        var lower = modelId.ToLowerInvariant();
        foreach (var (prefix, defaults) in ModelPrefixes)
            if (lower.StartsWith(prefix, StringComparison.Ordinal))
                return defaults;
        return null;
    }

    // Returns true only when the colon-suffix looks like an Ollama tag (contains at least one
    // letter). AWS Bedrock appends purely numeric version suffixes (":0", ":1") that should not
    // be mistaken for Ollama tags.
    private static bool HasOllamaStyleTag(string modelId)
    {
        var colon = modelId.LastIndexOf(':');
        if (colon < 0 || colon == modelId.Length - 1) return false;
        var tag = modelId.AsSpan(colon + 1);
        foreach (var c in tag)
            if (char.IsLetter(c)) return true;
        return false;
    }

    // Shared timeout applied to both HttpClient and the OpenAI SDK's per-request
    // NetworkTimeout so the two layers stay in sync. The SDK default is 100 s, which
    // is too short for long-running Magentic reasoning turns.
    private static readonly TimeSpan HttpClientTimeout = TimeSpan.FromMinutes(5);

    private static HttpClient BuildResilientClient(string? errorLogPath = null, EventEmitter? eventEmitter = null)
    {
        var handler = new ToolsRequiredRetryHandler
        {
            InnerHandler = new MessageNameStripHandler
            {
                InnerHandler = new FunctionStrictStripHandler
                {
                    InnerHandler = new FinishReasonNormalizerHandler
                    {
                        InnerHandler = new RawReasoningCaptureHandler(eventEmitter)
                        {
                            InnerHandler = new TransientRetryHandler(errorLogPath) { InnerHandler = new SocketsHttpHandler() }
                        }
                    }
                }
            }
        };
        return new HttpClient(handler) { Timeout = HttpClientTimeout };
    }
}

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
/// When the server returns a <c>Retry-After</c> header (common on 429 responses) that
/// value takes precedence over the computed back-off delay and is used without jitter so
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

/// <summary>
/// Strips the <c>strict</c> field from tool function definitions before sending to APIs
/// that don't support it (e.g. xAI). OpenAI SDK 2.x serialises <c>"strict": false</c>
/// on every function definition; providers that don't recognise the field return 400.
/// </summary>
internal sealed class FunctionStrictStripHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var stripped = StripFunctionStrict(body);
            if (!ReferenceEquals(stripped, body))
            {
                request.Content = new StringContent(stripped, Encoding.UTF8,
                    request.Content.Headers.ContentType?.MediaType ?? "application/json");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static string StripFunctionStrict(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var tools = node?["tools"]?.AsArray();
            if (tools is null) return json;

            bool changed = false;
            foreach (var tool in tools)
            {
                var fn = tool?["function"]?.AsObject();
                if (fn is not null && fn.ContainsKey("strict"))
                {
                    fn.Remove("strict");
                    changed = true;
                }
            }

            return changed ? node!.ToJsonString() : json;
        }
        catch
        {
            return json; // pass through unchanged on any parse error
        }
    }
}

/// <summary>
/// Captures raw <c>reasoning_content</c> from non-streaming (JSON) chat completion responses
/// and emits an <c>http_reasoning</c> event to the session event log.
///
/// <para>
/// xAI models populate a <c>choices[*].message.reasoning_content</c> field in the JSON response
/// body. This handler extracts that field at the HTTP layer — before the OpenAI SDK deserializes
/// the response — so the raw wire-level text can be compared against what
/// <c>TextReasoningContent</c> surfaces after SDK processing.
/// </para>
///
/// <para>
/// Positioning in the handler chain: inner to <see cref="FinishReasonNormalizerHandler"/> so
/// it sees the body before that handler consumes the stream. After reading, it rebuilds
/// <c>response.Content</c> as a <see cref="StringContent"/> so the outer handlers can still
/// read the body.
/// </para>
///
/// <para>
/// Skips SSE (streaming) responses — those do not carry <c>message.reasoning_content</c>.
/// Emits fire-and-forget: never throws, never blocks the request pipeline.
/// </para>
/// </summary>
internal sealed class RawReasoningCaptureHandler(EventEmitter? eventEmitter) : DelegatingHandler
{
    private const int MaxReasoningChars = 16_000;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.Content is null || eventEmitter is null) return response;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return response;

        // Read and buffer the body so this handler AND the outer FinishReasonNormalizerHandler
        // can both consume it (the underlying stream from TransientRetryHandler is read-once).
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Content = new StringContent(body, Encoding.UTF8,
            response.Content.Headers.ContentType?.MediaType ?? "application/json");

        // Fire-and-forget — EmitAsync never throws.
        TryCaptureReasoning(body, request.RequestUri?.Host ?? "unknown");

        return response;
    }

    private void TryCaptureReasoning(string body, string host)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is null) return;

            var model   = node["model"]?.GetValue<string>();
            var choices = node["choices"]?.AsArray();
            if (choices is null) return;

            int? reasoningTokens = null;
            try
            {
                reasoningTokens = node["usage"]?
                    ["completion_tokens_details"]?
                    ["reasoning_tokens"]?
                    .GetValue<int>();
            }
            catch { /* field absent or wrong type — leave null */ }

            var sb = new StringBuilder();
            foreach (var choice in choices)
            {
                var rc = choice?["message"]?["reasoning_content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(rc)) sb.Append(rc);
            }

            if (sb.Length == 0) return;

            var text = sb.ToString();
            var truncated = text.Length > MaxReasoningChars
                ? text[..MaxReasoningChars] + $"\n[TRUNCATED — {text.Length:N0} chars total]"
                : text;

            _ = eventEmitter!.EmitAsync("http_reasoning",
                agent:   null,
                turn:    null,
                payload: new
                {
                    model,
                    source           = "reasoning_content",
                    text             = truncated,
                    reasoning_tokens = reasoningTokens,
                    host,
                });
        }
        catch { /* never let capture crash the request pipeline */ }
    }
}

/// <summary>
/// Normalizes empty or missing <c>finish_reason</c> values in chat completion responses.
/// Some providers (e.g. xAI reasoning models) return <c>"finish_reason": ""</c> on intermediate
/// or reasoning-only choices. The OpenAI SDK's deserializer throws
/// <see cref="ArgumentOutOfRangeException"/> on any value it doesn't recognise, including the
/// empty string. This handler rewrites <c>""</c> to <c>"stop"</c> so the SDK can proceed.
/// </summary>
internal sealed class FinishReasonNormalizerHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.Content is null) return response;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return response;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var patched = PatchFinishReason(body);
        if (ReferenceEquals(patched, body)) return response;

        response.Content = new StringContent(patched, Encoding.UTF8,
            response.Content.Headers.ContentType?.MediaType ?? "application/json");
        return response;
    }

    private static string PatchFinishReason(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var choices = node?["choices"]?.AsArray();
            if (choices is null) return json;

            bool changed = false;
            foreach (var choice in choices)
            {
                var fr = choice?["finish_reason"];
                if (fr is not null && fr.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && string.IsNullOrEmpty(fr.GetValue<string>()))
                {
                    choice!.AsObject()["finish_reason"] = JsonNode.Parse("\"stop\"");
                    changed = true;
                }
            }

            return changed ? node!.ToJsonString() : json;
        }
        catch
        {
            return json;
        }
    }
}

/// <summary>
/// Strips the <c>name</c> field from non-user messages before sending to APIs
/// that only allow <c>name</c> on <c>user</c> role messages (e.g. xAI).
/// MAF sets <c>name</c> on assistant messages for agent identification, which
/// causes a 400 on strict providers.
/// </summary>
internal sealed class MessageNameStripHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var stripped = StripNonUserNames(body);
            if (!ReferenceEquals(stripped, body))
            {
                request.Content = new StringContent(stripped, Encoding.UTF8,
                    request.Content.Headers.ContentType?.MediaType ?? "application/json");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static string StripNonUserNames(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var messages = node?["messages"]?.AsArray();
            if (messages is null) return json;

            bool changed = false;
            foreach (var msg in messages)
            {
                var role = msg?["role"]?.GetValue<string>();
                if (role != "user" && msg?.AsObject().ContainsKey("name") == true)
                {
                    msg.AsObject().Remove("name");
                    changed = true;
                }
            }

            return changed ? node!.ToJsonString() : json;
        }
        catch
        {
            return json; // pass through unchanged on any parse error
        }
    }
}

/// <summary>
/// Detects the LiteLLM/Bedrock "tools= param required" 400 error and retries the request
/// with a no-op placeholder tool injected, matching what <c>litellm.modify_params = True</c>
/// does on the proxy side.
///
/// <para>
/// Bedrock requires the <c>tools</c> array to be present whenever any tool-calling-related
/// parameter is included in the request. When fuseraft-cli is pointed at a LiteLLM proxy
/// fronting Bedrock, and the proxy cannot be reconfigured, this handler intercepts the 400
/// and retries with a minimal dummy tool so the provider accepts the request.
/// </para>
///
/// <para>
/// The handler only retries when the request body contained no tools (empty or absent array).
/// If tools were already present the error has a different root cause and the original 400
/// is returned as-is.
/// </para>
/// </summary>
internal sealed class ToolsRequiredRetryHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the body before sending so we can patch and re-send on error.
        string? originalBody = null;
        string mediaType = "application/json";
        if (request.Content is not null)
        {
            mediaType = request.Content.Headers.ContentType?.MediaType ?? mediaType;
            originalBody = await request.Content.ReadAsStringAsync(cancellationToken);
            request.Content = new StringContent(originalBody, Encoding.UTF8, mediaType);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.BadRequest || originalBody is null)
            return response;

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        // Rebuild so the caller can still read the body.
        response.Content = new StringContent(errorBody, Encoding.UTF8,
            response.Content.Headers.ContentType?.MediaType ?? "application/json");

        if (!errorBody.Contains("tools=", StringComparison.Ordinal))
            return response;

        var patched = InjectNoOpTool(originalBody);
        if (patched is null)
            return response;

        Console.Error.WriteLine("[tools-retry] Bedrock/LiteLLM requires tools= — injecting no-op placeholder and retrying.");
        request.Content = new StringContent(patched, Encoding.UTF8, mediaType);
        return await base.SendAsync(request, cancellationToken);
    }

    private static string? InjectNoOpTool(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return null;

            // Only inject when tools is absent or empty — if tools are already present
            // the error has a different root cause and we should not retry.
            if (node["tools"] is JsonArray existing && existing.Count > 0)
                return null;

            node["tools"] = new JsonArray { BuildNoOpTool() };
            return node.ToJsonString();
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode BuildNoOpTool() =>
        JsonNode.Parse("""
        {
          "type": "function",
          "function": {
            "name": "no_op",
            "description": "Placeholder required by this provider.",
            "parameters": { "type": "object", "properties": {} }
          }
        }
        """)!;
}

/// <summary>
/// Wraps a network <see cref="Stream"/> and throws <see cref="TimeoutException"/> if the
/// SSE stream stops delivering real content events for longer than the configured idle window.
///
/// <para>
/// <c>HttpClient.Timeout</c> only covers time-to-first-byte. Once an SSE connection is open
/// the body can block indefinitely. A naive byte-level idle timer is defeated by keep-alive
/// ping events that providers (e.g. Anthropic) send every ~20–30 s; those pings deliver bytes
/// without any model output, silently resetting a byte-level timer forever.
/// </para>
///
/// <para>
/// This wrapper parses the SSE framing (field lines separated by blank lines) and maintains
/// two independent timers:
/// <list type="bullet">
///   <item><b>Byte-level</b> — <see cref="ByteIdleTimeout"/> (2 min): fires when the TCP
///     connection delivers no bytes at all, indicating a dead socket.</item>
///   <item><b>Content-event-level</b> — <paramref name="contentIdleTimeout"/> (default 5 min):
///     fires when no non-ping SSE event with a <c>data:</c> field has been received. Ping
///     events (<c>event: ping</c>) and bare comment lines (<c>: …</c>) do NOT reset this
///     timer, so a stalled model is detected even while keep-alives continue.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class SseEventIdleTimeoutStream(Stream inner, TimeSpan contentIdleTimeout) : Stream
{
    // Byte-level deadline: if the TCP socket delivers nothing at all for this long, the
    // connection is dead regardless of SSE state.
    private static readonly TimeSpan ByteIdleTimeout = TimeSpan.FromSeconds(120);

    // Track when we last saw a non-ping SSE data event.
    private DateTime _lastContentEventAt = DateTime.UtcNow;

    // SSE line-parse state.
    private readonly byte[] _lineBuf      = new byte[512];
    private int              _lineLen      = 0;
    private bool             _prevWasNl    = false;  // true when previous byte was '\n'
    private bool             _inPingEvent  = false;  // current SSE event has "event: ping"
    private bool             _hasDataLine  = false;  // current SSE event has at least one "data:" line

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        using var byteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        byteCts.CancelAfter(ByteIdleTimeout);
        int n;
        try
        {
            n = await inner.ReadAsync(buffer, offset, count, byteCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Streaming idle timeout: no bytes received for {ByteIdleTimeout.TotalSeconds:0}s. " +
                "The API connection appears to be dead.");
        }
        if (n > 0) CheckContentIdle(buffer.AsSpan(offset, n));
        return n;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var byteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        byteCts.CancelAfter(ByteIdleTimeout);
        int n;
        try
        {
            n = await inner.ReadAsync(buffer, byteCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Streaming idle timeout: no bytes received for {ByteIdleTimeout.TotalSeconds:0}s. " +
                "The API connection appears to be dead.");
        }
        if (n > 0) CheckContentIdle(buffer.Span[..n]);
        return n;
    }

    // Parse bytes into SSE lines, detect event boundaries and ping events, then check whether
    // the content-idle window has been exceeded.
    private void CheckContentIdle(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (b == (byte)'\n')
            {
                if (_prevWasNl || _lineLen == 0)
                {
                    // Blank line → SSE event boundary.
                    // Count as a content event only when it has a data: field and is not a ping.
                    if (_hasDataLine && !_inPingEvent)
                        _lastContentEventAt = DateTime.UtcNow;
                    _inPingEvent = false;
                    _hasDataLine = false;
                    _lineLen     = 0;
                }
                else
                {
                    // End of a field line — strip trailing \r and classify.
                    int len = _lineLen;
                    if (len > 0 && _lineBuf[len - 1] == (byte)'\r') len--;
                    ClassifyLine(_lineBuf.AsSpan(0, len));
                    _lineLen = 0;
                }
                _prevWasNl = true;
            }
            else
            {
                _prevWasNl = false;
                if (_lineLen < _lineBuf.Length)
                    _lineBuf[_lineLen++] = b;
            }
        }

        if (DateTime.UtcNow - _lastContentEventAt > contentIdleTimeout)
            throw new TimeoutException(
                $"Streaming content idle timeout: no non-ping SSE event received for " +
                $"{contentIdleTimeout.TotalMinutes:0} minute(s). " +
                "Keep-alive pings are flowing but the model appears to have stalled.");
    }

    // Sets _inPingEvent or _hasDataLine based on the SSE field line.
    private void ClassifyLine(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty) return;

        // SSE comment (":" prefix) — treat as keep-alive, do nothing.
        if (line[0] == (byte)':') return;

        // Cheaply decode — field names are ASCII.
        int colon = line.IndexOf((byte)':');
        if (colon < 0) return;

        var field = System.Text.Encoding.ASCII.GetString(line[..colon]).Trim();
        var value = System.Text.Encoding.ASCII.GetString(line[(colon + 1)..]).Trim();

        if (field.Equals("event", StringComparison.OrdinalIgnoreCase) &&
            value.Equals("ping", StringComparison.OrdinalIgnoreCase))
            _inPingEvent = true;

        if (field.Equals("data", StringComparison.OrdinalIgnoreCase))
            _hasDataLine = true;
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin)        => throw new NotSupportedException();
    public override void SetLength(long value)                       => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
