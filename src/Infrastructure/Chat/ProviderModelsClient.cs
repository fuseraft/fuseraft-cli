using System.Net.Http.Headers;
using System.Text.Json;

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
    /// <summary>
    /// Fetches available model IDs from the provider's models endpoint.
    /// Throws <see cref="ProviderConnectException"/> when the connection itself fails, or
    /// <see cref="InvalidOperationException"/> on HTTP error statuses or unexpected response shape.
    /// </summary>
    public static async Task<List<string>> FetchAsync(
        string endpoint, string apiKey, bool isOllama, CancellationToken cancellationToken = default)
    {
        var url = isOllama ? $"{endpoint}/api/tags" : $"{endpoint}/models";

        using var http = new HttpClient();
        if (!string.IsNullOrEmpty(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, cancellationToken);
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
}
