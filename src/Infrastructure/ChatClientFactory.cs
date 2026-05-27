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
    /// When multiple API keys are configured (via <c>ApiKeys</c> / <c>ApiKeyEnvVars</c>),
    /// returns a <see cref="KeyPoolChatClient"/> that rotates keys on 429.
    /// </summary>
    public IChatClient Create(ModelConfig config)
    {
        config = Resolve(config);

        // Primary API key — optional for Ollama. Literal ApiKey takes precedence over env-var lookup.
        var primaryKey = !string.IsNullOrEmpty(config.ApiKey)
            ? config.ApiKey
            : string.IsNullOrEmpty(config.ApiKeyEnvVar)
                ? string.Empty
                : Environment.GetEnvironmentVariable(config.ApiKeyEnvVar)
                  ?? throw new InvalidOperationException(
                      $"API key environment variable '{config.ApiKeyEnvVar}' is not set " +
                      $"(model: '{config.ModelId}', provider: '{config.Provider}').");

        // Build a key pool when multiple keys are configured
        var primary = BuildPool(config, primaryKey) ?? CreateCore(config, primaryKey);

        // Wrap with a fallover chain when one or more fallover models are configured.
        // Each fallover entry goes through the full Create() pipeline (including its own
        // key pool), so per-entry ApiKeys and ApiKeyEnvVars are fully supported.
        if (config.FalloverModels is { Count: > 0 })
        {
            var chain = new IChatClient[config.FalloverModels.Count + 1];
            chain[0] = primary;
            for (int i = 0; i < config.FalloverModels.Count; i++)
                chain[i + 1] = Create(config.FalloverModels[i]);
            var falloverOn = ProviderErrorClassifier.ParseFalloverOn(config.FalloverOn);
            return new FalloverChatClient(chain, falloverOn);
        }

        return primary;
    }

    // Collects all unique API keys from the config (primary + ApiKeys list + ApiKeyEnvVars list).
    // Returns a KeyPoolChatClient when >1 distinct key is available; null otherwise.
    private KeyPoolChatClient? BuildPool(ModelConfig config, string primaryKey)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keys = new List<string>();

        void Add(string? k)
        {
            if (!string.IsNullOrWhiteSpace(k) && seen.Add(k!))
                keys.Add(k!);
        }

        Add(primaryKey);
        if (config.ApiKeys is not null)
            foreach (var k in config.ApiKeys) Add(k);
        if (config.ApiKeyEnvVars is not null)
            foreach (var varName in config.ApiKeyEnvVars)
                Add(Environment.GetEnvironmentVariable(varName));

        if (keys.Count <= 1) return null;

        var slots = keys.Select(k => CreateCore(config, k)).ToArray();
        return new KeyPoolChatClient(slots);
    }

    // Builds a single IChatClient for a resolved config + explicit apiKey string.
    private IChatClient CreateCore(ModelConfig config, string apiKey)
    {
        var provider = config.Provider.Trim().ToLowerInvariant();
        var transport = new HttpClientPipelineTransport(_httpClient);

        switch (provider)
        {
            case "azure":
                if (string.IsNullOrEmpty(config.Endpoint))
                    throw new InvalidOperationException(
                        $"Provider 'azure' requires Endpoint to be set (deployment: '{config.ModelId}').");
                if (string.IsNullOrEmpty(apiKey))
                    throw new InvalidOperationException(
                        $"No API key available for Azure deployment '{config.ModelId}' at '{config.Endpoint}'. " +
                        $"Run 'fuseraft repl' and complete the setup wizard, or add \"apiKeyEnvVar\": \"<VAR>\" to ~/.fuseraft/config.");
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
                if (string.IsNullOrEmpty(apiKey))
                    throw new InvalidOperationException(
                        $"No API key available for model '{config.ModelId}' at '{config.Endpoint}'. " +
                        $"Run 'fuseraft repl' and complete the setup wizard, or add \"apiKeyEnvVar\": \"<VAR>\" to ~/.fuseraft/config.");
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
    // is too short for long-running Magentic reasoning turns. Raised to 20 min so that
    // reasoning models with large contexts (1 M+ token requests) can complete without
    // hitting the timeout and triggering the 4-retry chain unnecessarily.
    private static readonly TimeSpan HttpClientTimeout = TimeSpan.FromMinutes(20);

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

// Handler classes extracted to src/Infrastructure/Http/:
//   TransientRetryHandler          — retry + SSE idle-timeout wrapping
//   FunctionStrictStripHandler      — strips "strict" from tool definitions
//   RawReasoningCaptureHandler      — captures xAI reasoning_content field
//   FinishReasonNormalizerHandler   — normalizes empty finish_reason values
//   MessageNameStripHandler         — strips name field from non-user messages
//   ToolsRequiredRetryHandler       — injects no-op tool for Bedrock/LiteLLM
//   SseEventIdleTimeoutStream       — ping-aware SSE content idle timer

