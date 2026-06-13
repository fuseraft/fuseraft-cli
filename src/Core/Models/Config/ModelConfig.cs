using System.ComponentModel;

namespace fuseraft.Core.Models.Config;

/// <summary>
/// Configuration for the LLM backend used by an agent or strategy.
///
/// <para>
/// Can be specified as a plain string in JSON — <c>"Model": "gpt-4o"</c> — in which case
/// the provider, endpoint, and API-key environment variable are auto-detected from the
/// model ID prefix.  Named aliases defined in <c>OrchestrationConfig.Models</c> are
/// also resolved by name.  For full manual control use the object form.
/// </para>
///
/// <para>Auto-detected providers and their default env vars:</para>
/// <list type="bullet">
///   <item><c>gpt-*</c>, <c>o1*</c>, <c>o3*</c>, <c>o4*</c> → openai / <c>OPENAI_API_KEY</c></item>
///   <item><c>grok-*</c> → openai-compat xAI / <c>XAI_API_KEY</c></item>
///   <item><c>claude-*</c> → openai-compat Anthropic / <c>ANTHROPIC_API_KEY</c></item>
///   <item><c>gemini-*</c>, <c>learnlm-*</c> → google / <c>GOOGLE_AI_API_KEY</c></item>
///   <item><c>mistral-*</c>, <c>mixtral-*</c>, <c>codestral-*</c>, <c>pixtral-*</c> → mistral / <c>MISTRAL_API_KEY</c></item>
///   <item><c>deepseek-*</c> → openai-compat DeepSeek / <c>DEEPSEEK_API_KEY</c></item>
///   <item><c>llama*</c>, <c>phi*</c>, <c>qwen*</c>, <c>gemma*</c>, <c>*:*</c> → ollama (local, no key)</item>
/// </list>
/// </summary>
[TypeConverter(typeof(ModelConfigTypeConverter))]
public record ModelConfig
{
    /// <summary>
    /// The model identifier (e.g. "gpt-4o", "grok-4-1-fast-reasoning", "llama3.2").
    /// When this is the only field set, the other fields are auto-detected.
    /// </summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>
    /// LLM provider. Auto-detected from <see cref="ModelId"/> when omitted.
    /// Supported values: <c>openai</c> (default, also for any OpenAI-compatible API),
    /// <c>azure</c>, <c>google</c>, <c>mistral</c>, <c>ollama</c>.
    /// </summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// API base URL. Auto-detected from <see cref="Provider"/> when omitted.
    /// Required for <c>azure</c> (e.g. <c>https://my-resource.openai.azure.com/</c>).
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Name of the environment variable that holds the API key.
    /// Auto-detected from <see cref="Provider"/> when omitted.
    /// Leave empty for <c>ollama</c> (no key required).
    /// </summary>
    public string ApiKeyEnvVar { get; init; } = string.Empty;

    /// <summary>
    /// Literal API key value. When set, takes precedence over <see cref="ApiKeyEnvVar"/>.
    /// Used by the REPL when the user has configured a key in ~/.fuseraft/config.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Additional literal API keys for round-robin pool rotation.
    /// When more than one key is available (across <see cref="ApiKey"/>, <see cref="ApiKeyEnvVar"/>,
    /// <c>ApiKeys</c>, and <c>ApiKeyEnvVars</c>), a 429 on one key automatically rotates to the next.
    /// </summary>
    public List<string>? ApiKeys { get; init; }

    /// <summary>
    /// Additional environment variable names each holding an API key, for pool rotation.
    /// Resolved at request time; duplicates (same resolved value as other sources) are skipped.
    /// </summary>
    public List<string>? ApiKeyEnvVars { get; init; }

    /// <summary>
    /// Maximum tokens to generate per response. 0 = use model default.
    /// </summary>
    public int MaxTokens { get; init; } = 0;

    /// <summary>
    /// Maximum tokens allowed in the prompt sent to this model (the context window input limit).
    /// When set, the agent middleware estimates the token count before each API call and throws
    /// a clear exception if the budget would be exceeded — preventing expensive failed requests.
    /// Tool schemas are included in the estimate alongside message content.
    /// Set this to ~85% of the model's advertised limit to leave headroom for the model's
    /// response. 0 = no limit enforced (not recommended for production).
    /// </summary>
    public int MaxContextTokens { get; init; } = 0;

    /// <summary>
    /// Maximum serialized request body size in bytes. When set, the agent middleware
    /// estimates the outgoing JSON payload size before each API call and throws if it would
    /// exceed this limit — preventing HTTP 413 errors from upstream proxies (e.g. nginx).
    /// A conservative estimate: set to the proxy's <c>client_max_body_size</c> minus ~10%
    /// headroom. 0 = no limit enforced.
    /// </summary>
    public long MaxPayloadBytes { get; init; } = 0;

    /// <summary>
    /// Sampling temperature (0.0–2.0). Lower = more deterministic.
    /// Omit (or set to null) for reasoning models that reject this parameter.
    /// </summary>
    public double? Temperature { get; init; } = null;

    /// <summary>
    /// Reasoning effort level for models that support it (e.g. <c>grok-4.3</c>).
    /// Accepted values: <c>none</c>, <c>low</c>, <c>medium</c>, <c>high</c>.
    /// Injected as <c>"reasoning": {"effort": "..."}</c> in the request body.
    /// Omit for models that do not support the <c>reasoning</c> parameter.
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Ordered list of fallover models to try when this model fails with a classifiable error.
    /// Each entry supports the same shorthand as <see cref="ModelId"/> (a plain string in YAML).
    /// The primary model is always tried first; fallover models are tried in order.
    /// </summary>
    public List<ModelConfig>? FalloverModels { get; init; }

    /// <summary>
    /// Error reasons that trigger a fallover attempt. When omitted, all recoverable reasons
    /// are used: <c>RateLimit</c>, <c>ContextExceeded</c>, <c>QuotaExceeded</c>, <c>ServerError</c>.
    /// <c>AuthError</c> is never fallover-able — it indicates a permanent configuration problem.
    /// Only relevant when <see cref="FalloverModels"/> is set.
    /// </summary>
    public List<string>? FalloverOn { get; init; }
}

/// <summary>
/// Allows <see cref="ModelConfig"/> to be specified as a plain string in JSON/config
/// (e.g. <c>"Model": "gpt-4o"</c>), which is desugared to
/// <c>new ModelConfig { ModelId = "gpt-4o" }</c>.
/// The <see cref="fuseraft.Infrastructure.ChatClientFactory"/> then auto-detects the
/// provider, endpoint, and API key environment variable from the model ID prefix.
/// </summary>
public sealed class ModelConfigTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object ConvertFrom(ITypeDescriptorContext? context,
        System.Globalization.CultureInfo? culture, object value)
    {
        if (value is string modelId)
            return new ModelConfig { ModelId = modelId };

        return base.ConvertFrom(context, culture, value)!;
    }
}
