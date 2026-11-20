using System.Text.Json;

namespace fuseraft.Infrastructure.Chat;

/// <summary>A model's input limit in tokens, and where it was found.</summary>
public sealed record ModelInputLimit(int Tokens, string Source);

/// <summary>
/// Reads a model's input limit from its provider's own model metadata: the model's <c>max_input_tokens</c> where the
/// provider reports one, otherwise 90% of its context window, which leaves room for the reply.
/// <list type="bullet">
///   <item>Anthropic: <c>GET /v1/models/{id}</c>, <c>max_input_tokens</c>.</item>
///   <item>OpenAI-compatible hosts that list it on <c>GET /models</c>: xAI and OpenRouter (<c>context_length</c>),
///     Groq (<c>context_window</c>), Mistral and LM Studio (<c>max_context_length</c>), vLLM (<c>max_model_len</c>).</item>
///   <item>A LiteLLM gateway: <c>GET /model_group/info?model_group={id}</c>, <c>max_input_tokens</c>, which any key
///     may read.</item>
/// </list>
/// Returns null when the provider reports neither (OpenAI, Ollama) or can't be reached.
/// </summary>
internal static class ModelLimitsClient
{
    /// <summary>The share of a context window counted as input when only the window is known.</summary>
    internal const double ContextWindowInputShare = 0.9;

    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private static readonly HttpClient Http = new() { Timeout = Timeout };

    private static readonly string[] WindowFields = ["context_length", "context_window", "max_context_length", "max_model_len"];

    public static async Task<ModelInputLimit?> FetchAsync(
        string provider, string endpoint, string modelId, string apiKey, CancellationToken ct, HttpMessageInvoker? http = null)
    {
        http ??= Http;
        endpoint = endpoint.TrimEnd('/');
        try
        {
            if (provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                return null;

            if (provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            {
                using var model = await GetAsync(http, $"{endpoint}/v1/models/{Uri.EscapeDataString(modelId)}", apiKey, anthropic: true, ct);
                return model is null ? null : FromModel(model.RootElement, "the Anthropic models API");
            }

            using (var list = await GetAsync(http, $"{endpoint}/models", apiKey, anthropic: false, ct))
            {
                if (list is not null && Entries(list.RootElement, "data")
                        .FirstOrDefault(m => Str(m, "id") == modelId) is { ValueKind: JsonValueKind.Object } entry
                    && FromModel(entry, "the provider's model list") is { } listed)
                    return listed;
            }

            // LiteLLM serves its management routes at the root, beside /v1.
            var root = endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? endpoint[..^3] : endpoint;
            using var group = await GetAsync(
                http, $"{root}/model_group/info?model_group={Uri.EscapeDataString(modelId)}", apiKey, anthropic: false, ct);
            return group is not null && Entries(group.RootElement, "data")
                    .FirstOrDefault(m => Str(m, "model_group") == modelId) is { ValueKind: JsonValueKind.Object } info
                ? FromModel(info, "the LiteLLM gateway")
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The limit one model entry states: its <c>max_input_tokens</c>, else 90% of its context window.</summary>
    internal static ModelInputLimit? FromModel(JsonElement model, string where)
    {
        if (Int(model, "max_input_tokens") is { } input)
            return new ModelInputLimit(input, $"max_input_tokens from {where}");
        foreach (var field in WindowFields)
            if (Int(model, field) is { } window)
                return new ModelInputLimit((int)(window * ContextWindowInputShare), $"90% of {field} from {where}");
        return null;
    }

    private static async Task<JsonDocument?> GetAsync(HttpMessageInvoker http, string url, string apiKey, bool anthropic, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (anthropic)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            return null;
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
    }

    private static IEnumerable<JsonElement> Entries(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
            : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var n) && n > 0
            ? (int)Math.Min(n, int.MaxValue)
            : null;
}
