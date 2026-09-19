using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Authentication;
using fuseraft.Infrastructure.Mcp;

namespace FuseraftCli.Tests;

/// <summary>
/// Exercises the loopback-listener mechanics <see cref="McpOAuthBrowserFlow"/> owns (the SDK
/// drives everything before and after this point — token exchange, PKCE, dynamic client
/// registration). No real browser or OAuth server is involved: this simulates the browser's
/// final redirect with a plain HTTP GET against the callback URI.
/// </summary>
[Collection("LoopbackPorts")]
public sealed class McpOAuthBrowserFlowTests
{
    [Fact]
    public async Task SuccessfulCallback_ReturnsCodeAndState()
    {
        var port = GetFreeLoopbackPort();
        var flow = new McpOAuthBrowserFlow("TestServer", openBrowser: static _ => { });
        var context = new AuthorizationCallbackContext
        {
            AuthorizationUri = new Uri("https://example.com/authorize?client_id=x"),
            RedirectUri      = new Uri($"http://localhost:{port}/callback"),
        };

        var resultTask = flow.HandleAuthorizationUrlAsync(context, CancellationToken.None);

        using var http = new HttpClient();
        var callbackResponse = await http.GetAsync($"http://localhost:{port}/callback?code=abc123&state=xyz");
        Assert.True(callbackResponse.IsSuccessStatusCode);

        var result = await resultTask;

        Assert.NotNull(result);
        Assert.Equal("abc123", result!.Code);
        Assert.Equal("xyz", result.State);
    }

    [Fact]
    public async Task ErrorCallback_ReturnsNull()
    {
        var port = GetFreeLoopbackPort();
        var flow = new McpOAuthBrowserFlow("TestServer", openBrowser: static _ => { });
        var context = new AuthorizationCallbackContext
        {
            AuthorizationUri = new Uri("https://example.com/authorize?client_id=x"),
            RedirectUri      = new Uri($"http://localhost:{port}/callback"),
        };

        var resultTask = flow.HandleAuthorizationUrlAsync(context, CancellationToken.None);

        using var http = new HttpClient();
        await http.GetAsync($"http://localhost:{port}/callback?error=access_denied");

        Assert.Null(await resultTask);
    }

    [Fact]
    public async Task Cancellation_UnblocksTheListener()
    {
        var port = GetFreeLoopbackPort();
        var flow = new McpOAuthBrowserFlow("TestServer", openBrowser: static _ => { });
        var context = new AuthorizationCallbackContext
        {
            AuthorizationUri = new Uri("https://example.com/authorize?client_id=x"),
            RedirectUri      = new Uri($"http://localhost:{port}/callback"),
        };

        using var cts = new CancellationTokenSource();
        var resultTask = flow.HandleAuthorizationUrlAsync(context, cts.Token);

        cts.Cancel();

        // Must unblock (via Stop()) rather than hang forever waiting for a callback that never
        // arrives — either an OperationCanceledException or a null result is an acceptable
        // resolution; hanging is the only failure mode this guards against.
        var completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(resultTask, completed);
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // Regression: HttpListener.Close() re-binds the endpoint it just released while unregistering
    // its prefix; if anything holds the port by then it throws "Address already in use". That
    // used to escape HandleAuthorizationUrlAsync *after* the code was captured, failing a login
    // that had actually succeeded. Reproduced deterministically here: stop the listener, take its
    // port with raw sockets, then close it. Both loopback families are taken because "localhost"
    // may resolve to either ::1 or 127.0.0.1 depending on the machine.
    [Fact]
    public void ListenerCloser_SwallowsTheRebindFailureThatCloseCanThrow()
    {
        var port = GetFreeLoopbackPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        listener.Stop();

        var squatters = new List<TcpListener>();
        try
        {
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                try
                {
                    var squatter = new TcpListener(address, port);
                    squatter.Start();
                    squatters.Add(squatter);
                }
                catch (SocketException) { /* that family isn't available here */ }
            }
            Assert.NotEmpty(squatters);

            var closer = new McpOAuthBrowserFlow.ListenerCloser(listener);

            var ex = Record.Exception(closer.Dispose);

            Assert.Null(ex);
        }
        finally
        {
            foreach (var squatter in squatters) squatter.Stop();
        }
    }

    [Fact]
    public void ListenerCloser_IsSafeToDisposeTwiceAndOnAListenerThatNeverStarted()
    {
        var neverStarted = new McpOAuthBrowserFlow.ListenerCloser(new HttpListener());
        neverStarted.Dispose();
        neverStarted.Dispose();

        var port = GetFreeLoopbackPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var closer = new McpOAuthBrowserFlow.ListenerCloser(listener);
        closer.Dispose();
        closer.Dispose();

        Assert.False(listener.IsListening);
    }
}
