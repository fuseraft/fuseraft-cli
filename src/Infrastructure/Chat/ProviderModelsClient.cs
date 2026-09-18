using System.Net.Http.Headers;
using System.Text.Json;
using Anthropic.SDK;

namespace fuseraft.Infrastructure.Chat;

/// <summary>
/// Thrown when the request to the provider's models endpoint never got a response
/// (DNS/TCP/TLS failure). Callers can use this to tell "wrong path" failures (worth
/// retrying with a different endpoint shape) apart from "host unreachable" failures
/// (retrying a different path on the same host/port will fail identically).
/// </summary>
public sealed class ProviderConnectException(string message, Exception inner) : InvalidOperationException(message, inner);

public static class ProviderModelsClient
{
    // Shared across calls — both FetchAsync and FetchAnthropicAsync used to open (and
    // `using`-dispose) a fresh HttpClient per call, which under repeated `fuseraft models`
    // invocations or REPL setup-wizard retries risks socket exhaustion and skips whatever
    // retry/timeout policy a pooled client would apply. Per-call auth still varies (different
    // API keys), so it's carried on each HttpRequestMessage rather than DefaultRequestHeaders.
    private static readonly HttpClient _shared = new();

    /// <summary>
    /// Fetches available model IDs from the provider's models endpoint.
    /// Throws <see cref="ProviderConnectException"/> when the connection itself fails, or
    /// <see cref="InvalidOperationException"/> on HTTP error statuses or unexpected response shape.
    /// </summary>
    public static async Task<List<string>> FetchAsync(
        string endpoint, string apiKey, bool isOllama, CancellationToken cancellationToken = default)
    {
        var url = isOllama ? $"{endpoint}/api/tags" : $"{endpoint}/models";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await _shared.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ProviderConnectException($"Request to {url} failed: {ex.Message}", ex);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var snippet = body.Length > 200 ? body[..200] + "…" : body;
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
        }

        try
        {
            var json = JsonDocument.Parse(body);
            return isOllama
                ? [.. json.RootElement.GetProperty("models")
                    .EnumerateArray()
                    .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => id!)
                    .Order()]
                : [.. json.RootElement.GetProperty("data")
                    .EnumerateArray()
                    .Select(m => m.TryGetProperty("id", out var n) ? n.GetString() : null)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => id!)
                    .Order()];
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not parse models response: {ex.Message}", ex);
        }
    }

    // Native Anthropic Messages API base — matches ChatClientFactory.AnthropicDefaultEndpoint.
    private const string AnthropicDefaultEndpoint = "https://api.anthropic.com";

    /// <summary>
    /// Fetches available model IDs from Anthropic's native Models API (<c>GET /v1/models</c>,
    /// authenticated with <c>x-api-key</c>) via <see cref="AnthropicClient"/>. The generic
    /// OpenAI-compatible shape in <see cref="FetchAsync"/> does not apply here: the native
    /// endpoint (<see cref="ChatClientFactory.AnthropicDefaultEndpoint"/> in normal use, no
    /// trailing <c>/v1</c>) has no <c>/models</c> route, and could stop tolerating an
    /// <c>Authorization: Bearer</c> header at any time even where it happens to today.
    /// </summary>
    public static async Task<List<string>> FetchAnthropicAsync(
        string endpoint, string apiKey, CancellationToken cancellationToken = default)
    {
        var client = new AnthropicClient(apiKey, _shared);
        var trimmed = endpoint.TrimEnd('/');
        if (!string.IsNullOrEmpty(trimmed) && !trimmed.Equals(AnthropicDefaultEndpoint, StringComparison.OrdinalIgnoreCase))
            client.ApiUrlFormat = trimmed + "/{0}/{1}";

        Anthropic.SDK.Models.ModelList list;
        try
        {
            list = await client.Models.ListModelsAsync(beforeId: null, afterId: null, limit: 1000, ctx: cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderConnectException($"Request to {trimmed}/v1/models failed: {ex.Message}", ex);
        }

        return [.. list.Models.Select(m => m.Id).Order()];
    }
}
