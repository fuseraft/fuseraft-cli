using System.Net.Http.Headers;
using System.Text.Json;

namespace fuseraft.Infrastructure.Chat;

public static class ProviderModelsClient
{
    /// <summary>
    /// Fetches available model IDs from the provider's models endpoint.
    /// Throws <see cref="InvalidOperationException"/> on HTTP errors or unexpected response shape.
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
            throw new InvalidOperationException($"Request to {url} failed: {ex.Message}", ex);
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
