using System.Net;
using System.Net.Http.Headers;
using fuseraft.Core.Models;

namespace fuseraft.Cli;

/// <summary>
/// Probes each unique provider API endpoint referenced by a config to verify the configured
/// keys are valid before a session starts. Extracted from <see cref="OrchestratorBuilder"/> —
/// provider-connectivity probing is a distinct responsibility from config loading or
/// orchestrator construction, despite having lived in the same file.
/// </summary>
public static class ApiKeyValidator
{
    // Shared client for API-key validation probes — created once, never disposed.
    private static readonly HttpClient _validationHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Makes a lightweight <c>GET /models</c> call to each unique API endpoint in
    /// <paramref name="config"/> to verify the keys are valid before the session starts.
    /// Throws <see cref="InvalidOperationException"/> if any key is missing or rejected.
    /// </summary>
    public static async Task ValidateApiKeysAsync(
        OrchestrationConfig config,
        CancellationToken cancellationToken = default)
    {
        // Collect all ModelConfigs: one per agent + optional selection-strategy model
        // + optional Magentic manager model.
        // Resolve aliases against the Models registry first so agents that reference
        // a named alias (e.g. "fast") get the endpoint and API key from the alias.
        var models = config.Agents.Select(a => ResolveAlias(a.Model, config.Models))
            .Concat(config.Selection.Model is not null
                ? [ResolveAlias(config.Selection.Model, config.Models)]
                : Array.Empty<ModelConfig>())
            .Concat(config.Selection.Magentic?.Model is not null
                ? [ResolveAlias(config.Selection.Magentic.Model, config.Models)]
                : Array.Empty<ModelConfig>())
            .Where(m => !string.IsNullOrWhiteSpace(m.ApiKeyEnvVar))  // skip Ollama (no key)
            .GroupBy(m => m.ApiKeyEnvVar)   // deduplicate: only probe each key once
            .Select(g => g.First())
            .ToList();

        var http = _validationHttp;

        foreach (var model in models)
        {
            var apiKey = Environment.GetEnvironmentVariable(model.ApiKeyEnvVar);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    $"API key variable '{model.ApiKeyEnvVar}' is not set.");

            // Strip /chat/completions (or any path) to get the provider base URL.
            var uri    = new Uri(model.Endpoint.TrimEnd('/'));
            var baseUrl = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}";

            // Use a per-request message so keys from different providers don't bleed
            // across iterations via DefaultRequestHeaders.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"Could not reach API endpoint '{baseUrl}': {ex.Message}", ex);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException(
                    $"API key from '{model.ApiKeyEnvVar}' was rejected by the provider (HTTP 401). " +
                    $"Verify the key is current and has the correct permissions.");
        }
    }

    private static ModelConfig ResolveAlias(
        ModelConfig model,
        IReadOnlyDictionary<string, ModelConfig> registry)
    {
        if (registry.TryGetValue(model.ModelId, out var alias))
        {
            return alias with
            {
                Temperature = model.Temperature ?? alias.Temperature,
                MaxTokens   = model.MaxTokens > 0 ? model.MaxTokens : alias.MaxTokens
            };
        }
        return model;
    }
}
