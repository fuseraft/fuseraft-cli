using System.Net;
using System.Text;
using fuseraft.Infrastructure.Chat;

namespace FuseraftCli.Tests;

public class ModelLimitsClientTests
{
    // Answers each URL from a table; anything else is a 404. Records the requests it saw.
    private sealed class Routes(Dictionary<string, string> bodies) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(bodies.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static async Task<(ModelInputLimit? Limit, Routes Http)> FetchAsync(
        string provider, string endpoint, string model, Dictionary<string, string> bodies)
    {
        var routes = new Routes(bodies);
        using var invoker = new HttpMessageInvoker(routes);
        return (await ModelLimitsClient.FetchAsync(provider, endpoint, model, "test-key", CancellationToken.None, invoker), routes);
    }

    [Fact]
    public async Task Anthropic_UsesMaxInputTokens_WithItsOwnAuth()
    {
        var (limit, http) = await FetchAsync("anthropic", "https://api.anthropic.com", "claude-sonnet-5", new()
        {
            ["https://api.anthropic.com/v1/models/claude-sonnet-5"] = """{"id":"claude-sonnet-5","max_input_tokens":1000000,"max_tokens":64000}""",
        });

        Assert.Equal(1_000_000, limit!.Tokens);
        Assert.Contains("max_input_tokens", limit.Source);
        Assert.Equal("test-key", http.Seen[0].Headers.GetValues("x-api-key").Single());
        Assert.Null(http.Seen[0].Headers.Authorization);
    }

    [Fact]
    public async Task ModelList_WithAContextWindow_CountsNinetyPercentAsInput()
    {
        var (limit, http) = await FetchAsync("openai", "https://api.x.ai/v1", "grok-4.7", new()
        {
            ["https://api.x.ai/v1/models"] = """{"data":[{"id":"grok-3"},{"id":"grok-4.7","context_length":500000}]}""",
        });

        Assert.Equal(new ModelInputLimit(450_000, "90% of context_length from the provider's model list"), limit);
        Assert.Equal("Bearer", http.Seen[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task LiteLlmGateway_AnswersFromModelGroupInfo()
    {
        var (limit, _) = await FetchAsync("openai", "https://llm.example.com/v1", "grok-4.6", new()
        {
            ["https://llm.example.com/v1/models"] = """{"data":[{"id":"grok-4.6","object":"model"}]}""",
            ["https://llm.example.com/model_group/info?model_group=grok-4.6"] =
                """{"data":[{"model_group":"grok-4.6","max_input_tokens":500000,"max_output_tokens":64000}]}""",
        });

        Assert.Equal(new ModelInputLimit(500_000, "max_input_tokens from the LiteLLM gateway"), limit);
    }

    [Fact]
    public async Task NoLimitReported_IsNull()
    {
        var (limit, _) = await FetchAsync("openai", "https://api.openai.com/v1", "gpt-5", new()
        {
            ["https://api.openai.com/v1/models"] = """{"data":[{"id":"gpt-5","object":"model"}]}""",
        });

        Assert.Null(limit);
    }

    [Fact]
    public async Task GatewayWithoutALimit_IsNull()
    {
        var (limit, _) = await FetchAsync("openai", "https://llm.example.com/v1", "grok-4.6", new()
        {
            ["https://llm.example.com/model_group/info?model_group=grok-4.6"] =
                """{"data":[{"model_group":"grok-4.6","max_input_tokens":null}]}""",
        });

        Assert.Null(limit);
    }

    [Fact]
    public async Task Ollama_IsNotAsked()
    {
        var (limit, http) = await FetchAsync("ollama", "http://localhost:11434", "qwen3:8b", []);

        Assert.Null(limit);
        Assert.Empty(http.Seen);
    }

    [Fact]
    public async Task MalformedResponse_IsNull()
    {
        var (limit, _) = await FetchAsync("openai", "https://api.x.ai/v1", "grok-4.7", new()
        {
            ["https://api.x.ai/v1/models"] = "not json",
        });

        Assert.Null(limit);
    }
}
