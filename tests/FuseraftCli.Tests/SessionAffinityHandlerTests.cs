using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Context;

namespace FuseraftCli.Tests;

public class SessionAffinityHandlerTests
{
    private static async Task<HttpRequestMessage> SendAsync(string url, string? session, string? body = null)
    {
        var capture = new CaptureHandler();
        using var invoker = new HttpMessageInvoker(new SessionAffinityHandler { InnerHandler = capture });
        RequestSession.Id.Value = session;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            await invoker.SendAsync(request, CancellationToken.None);
        }
        finally
        {
            RequestSession.Id.Value = null;
        }
        return capture.Request!;
    }

    [Fact]
    public async Task Gateway_GetsTheLiteLlmSessionHeader()
    {
        var request = await SendAsync("https://llm.example.com/v1/chat/completions", "0e71da42");

        Assert.Equal("0e71da42", Assert.Single(request.Headers.GetValues(SessionAffinityHandler.LiteLlmHeader)));
        Assert.False(request.Headers.Contains(SessionAffinityHandler.XaiHeader));
    }

    [Fact]
    public async Task Xai_AlsoGetsItsConversationHeader()
    {
        var request = await SendAsync("https://api.x.ai/v1/chat/completions", "0e71da42");

        Assert.Equal("0e71da42", Assert.Single(request.Headers.GetValues(SessionAffinityHandler.XaiHeader)));
    }

    [Fact]
    public async Task LookalikeHost_IsNotXai()
    {
        var request = await SendAsync("https://notx.ai/v1/chat/completions", "0e71da42");

        Assert.False(request.Headers.Contains(SessionAffinityHandler.XaiHeader));
    }

    [Fact]
    public async Task GrokThroughAGateway_GetsItsConversationHeader()
    {
        var body = """{"messages":[{"role":"user","content":"model: claude"}],"tools":[],"model":"xai.grok-4.6","stream":true}""";

        var request = await SendAsync("https://llm.example.com/v1/chat/completions", "0e71da42", body);

        Assert.Equal("0e71da42", Assert.Single(request.Headers.GetValues(SessionAffinityHandler.XaiHeader)));
        Assert.Equal(body, await request.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task OtherModelsThroughAGateway_DoNotGetIt()
    {
        var request = await SendAsync("https://llm.example.com/v1/chat/completions", "0e71da42",
            """{"messages":[{"role":"user","content":"compare with grok"}],"model":"anthropic.claude-sonnet-5-5"}""");

        Assert.False(request.Headers.Contains(SessionAffinityHandler.XaiHeader));
    }

    [Theory]
    [InlineData("""{"model":"xai.grok-4.6"}""", "xai.grok-4.6")]
    [InlineData("""{"messages":[{"model":"nested"}],"model":"m"}""", "m")]
    [InlineData("""{"messages":[]}""", null)]
    [InlineData("""{"model":3}""", null)]
    [InlineData("""not json""", null)]
    [InlineData("""{"model":"cut""", null)]
    public void ModelOf_ReadsOnlyTheTopLevelField(string json, string? expected) =>
        Assert.Equal(expected, SessionAffinityHandler.ModelOf(System.Text.Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task NoSession_AddsNoHeaders()
    {
        var request = await SendAsync("https://api.x.ai/v1/chat/completions", null);

        Assert.False(request.Headers.Contains(SessionAffinityHandler.LiteLlmHeader));
        Assert.False(request.Headers.Contains(SessionAffinityHandler.XaiHeader));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
