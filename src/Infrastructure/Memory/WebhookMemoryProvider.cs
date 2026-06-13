using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Infrastructure.Memory;

/// <summary>
/// Memory provider that delegates load/save to a generic HTTP endpoint.
/// POST body for load: <c>{"agent": "&lt;name&gt;"}</c>
/// Expected response:  <c>{"block": "&lt;text&gt;"}</c>
/// POST body for save: <c>{"agent": "&lt;name&gt;", "history": [...]}</c>
/// Save is throttled to every <see cref="WebhookMemoryConfig.SaveEveryNTurns"/> calls.
/// Header values support <c>${ENV_VAR}</c> expansion.
/// </summary>
internal sealed class WebhookMemoryProvider : IMemoryProvider, IDisposable
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly WebhookMemoryConfig _cfg;
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _resolvedHeaders;
    private int _saveCalls;

    public WebhookMemoryProvider(WebhookMemoryConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds) };
        _resolvedHeaders = ResolveHeaders(cfg.Headers);
    }

    public async Task<string?> LoadAsync(string agentName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_cfg.LoadUrl)) return null;

        try
        {
            var body    = JsonSerializer.Serialize(new { agent = agentName }, _opts);
            using var req = BuildRequest(HttpMethod.Post, _cfg.LoadUrl, body);
            using var res = await _http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("block", out var blockEl))
            {
                var block = blockEl.GetString();
                return string.IsNullOrWhiteSpace(block) ? null : block;
            }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebhookMemoryProvider] Load failed for '{agentName}': {ex.Message}");
            return null;
        }
    }

    public async Task SaveAsync(string agentName, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_cfg.SaveUrl)) return;

        var n = System.Threading.Interlocked.Increment(ref _saveCalls);
        var every = Math.Max(1, _cfg.SaveEveryNTurns);
        if (n % every != 0) return;

        try
        {
            var messages = history.Select(m => new
            {
                role    = m.Role.Value,
                content = m.Text ?? string.Empty,
            });
            var body = JsonSerializer.Serialize(new { agent = agentName, history = messages }, _opts);
            using var req = BuildRequest(HttpMethod.Post, _cfg.SaveUrl, body);
            using var res = await _http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WebhookMemoryProvider] Save failed for '{agentName}': {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, string jsonBody)
    {
        var req = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        foreach (var (k, v) in _resolvedHeaders)
            req.Headers.TryAddWithoutValidation(k, v);
        return req;
    }

    private static Dictionary<string, string> ResolveHeaders(Dictionary<string, string> raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in raw)
            result[k] = Regex.Replace(v, @"\$\{([^}]+)\}", m =>
                Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? string.Empty);
        return result;
    }
}
