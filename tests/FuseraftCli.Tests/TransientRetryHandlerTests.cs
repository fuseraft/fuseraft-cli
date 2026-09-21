using System.Net;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure;

namespace FuseraftCli.Tests;

/// <summary>The retry budget comes from <see cref="TransportOptions"/>; back-off is captured, not slept.</summary>
public sealed class TransientRetryHandlerTests
{
    private sealed class AlwaysStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    private static async Task<(HttpResponseMessage Response, int Calls, List<TimeSpan> Delays)> SendAsync(
        HttpStatusCode status, TransportOptions? transport)
    {
        var inner  = new AlwaysStatusHandler(status);
        var delays = new List<TimeSpan>();
        var handler = new TransientRetryHandler(null, null, transport)
        {
            InnerHandler = inner,
            DelayAsync   = (d, _) => { delays.Add(d); return Task.CompletedTask; },
        };

        var response = await new HttpMessageInvoker(handler)
            .SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/chat"), CancellationToken.None);
        return (response, inner.Calls, delays);
    }

    [Fact]
    public async Task DefaultBudget_IsThreeRetries()
    {
        var (response, calls, delays) = await SendAsync(HttpStatusCode.ServiceUnavailable, transport: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(4, calls);
        Assert.Equal(3, delays.Count);
    }

    [Fact]
    public async Task ZeroRetries_SurfacesTheFirstFailureImmediately()
    {
        var (response, calls, delays) = await SendAsync(
            HttpStatusCode.TooManyRequests, TransportOptions.From(new UserConfig { MaxRetries = 0 }));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task ConfiguredBudget_IsHonoured()
    {
        var (_, calls, _) = await SendAsync(
            HttpStatusCode.BadGateway, TransportOptions.From(new UserConfig { MaxRetries = 6 }));

        Assert.Equal(7, calls);
    }

    [Fact]
    public async Task Backoff_IsCappedSoARaisedBudgetCannotProduceMultiMinuteSleeps()
    {
        var (_, _, delays) = await SendAsync(
            HttpStatusCode.ServiceUnavailable, TransportOptions.From(new UserConfig { MaxRetries = 10 }));

        Assert.Equal(10, delays.Count);
        // 60 s cap, ±20 % jitter — uncapped, the last attempts would sleep 512 s and 1024 s.
        Assert.All(delays, d => Assert.InRange(d.TotalSeconds, 1.6, 72.0));
        Assert.InRange(delays[^1].TotalSeconds, 48.0, 72.0);
    }
}
