using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Context;

namespace FuseraftCli.Tests;

public class SessionAffinityHandlerTests
{
    private static async Task<HttpRequestMessage> SendAsync(string url, string? session)
    {
        var capture = new CaptureHandler();
        using var invoker = new HttpMessageInvoker(new SessionAffinityHandler { InnerHandler = capture });
        RequestSession.Id.Value = session;
        try
        {
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, url), CancellationToken.None);
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
