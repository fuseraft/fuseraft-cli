using System.Net;
using System.Net.Sockets;

namespace FuseraftCli.Tests;

/// <summary>
/// A throwaway HTTP server on a loopback port for tests that need to observe exactly what a client
/// sent — headers, method, body — or script a redirect chain across two distinct origins (each
/// instance is its own origin, by port). Records every request before invoking the handler.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    internal sealed record Recorded(string Method, string Path, Dictionary<string, string> Headers, string Body);

    private readonly HttpListener _listener;
    private readonly Task _loop;

    public int Port { get; }
    public string Host { get; }
    public string BaseUrl => $"http://{Host}:{Port}";
    public List<Recorded> Requests { get; } = [];

    /// <param name="host">
    /// The loopback address to bind. The whole 127.0.0.0/8 range is loopback on Linux, so
    /// <c>127.0.0.2</c> gives a second, distinct <i>host</i> — not just a second port — for tests
    /// about host allowlists and cross-host redirects.
    /// </param>
    public LoopbackHttpServer(Func<HttpListenerRequest, HttpListenerResponse, Task> handler, string host = "127.0.0.1")
    {
        Host = host;
        // Picking a free port and then binding it is inherently racy: xUnit runs test classes in
        // parallel, so another server can take the port between the probe releasing it and this
        // listener starting ("Address already in use"). Retry on a fresh port instead of failing.
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Parse(host), 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (HttpListenerException) when (attempt < 25)
            {
                listener.Close();
            }
        }

        _loop = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in ctx.Request.Headers.AllKeys)
                    if (key is not null) headers[key] = ctx.Request.Headers[key] ?? "";

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    body = await reader.ReadToEndAsync();

                lock (Requests)
                    Requests.Add(new Recorded(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, headers, body));

                try { await handler(ctx.Request, ctx.Response); }
                catch { /* connection torn down mid-response */ }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        });
    }

    public bool SawHeader(string name, string? value = null)
    {
        lock (Requests)
            return Requests.Any(r => r.Headers.TryGetValue(name, out var v) && (value is null || v == value));
    }

    public List<Recorded> Snapshot()
    {
        lock (Requests) return [.. Requests];
    }

    public static Task RedirectTo(HttpListenerResponse res, string location, int status = 307)
    {
        res.StatusCode = status;
        res.RedirectLocation = location;
        return Task.CompletedTask;
    }

    public static Task Respond(HttpListenerResponse res, int status = 200, string body = "")
    {
        res.StatusCode = status;
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        res.ContentLength64 = bytes.Length;
        return res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    public async ValueTask DisposeAsync()
    {
        // Best-effort: HttpListener.Close() can throw "Address already in use" while unregistering
        // its prefix, and a teardown failure must not fail the test that already passed.
        try { _listener.Close(); } catch (HttpListenerException) { }
        try { await _loop; } catch { }
    }
}
