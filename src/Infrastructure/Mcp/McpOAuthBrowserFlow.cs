using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Web;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;

namespace fuseraft.Infrastructure.Mcp;

/// <summary>
/// Drives an interactive OAuth 2.1 authorization-code login for one MCP server: opens the
/// user's browser to the server's consent page, listens on the loopback redirect URI for the
/// callback, and hands the resulting code back to the SDK's <c>ClientOAuthProvider</c> to
/// exchange for tokens. Modeled on the MCP C# SDK's own <c>ProtectedMcpClient</c> sample
/// (github.com/modelcontextprotocol/csharp-sdk/tree/main/samples/ProtectedMcpClient), adapted to
/// log through fuseraft's <see cref="ILogger"/> pipeline instead of writing to the console
/// directly, and to make browser-launch work on Linux (which has no shell URL association).
/// </summary>
public sealed class McpOAuthBrowserFlow(string serverName, ILogger? logger = null, Action<Uri>? openBrowser = null)
{
    /// <summary>
    /// Matches <c>ClientOAuthOptions.AuthorizationCallbackHandler</c>'s delegate shape — assign
    /// this method directly to that property.
    /// </summary>
    public async Task<AuthorizationResult?> HandleAuthorizationUrlAsync(
        AuthorizationCallbackContext context, CancellationToken cancellationToken)
    {
        var authorizationUrl = context.AuthorizationUri;
        var redirectUri      = context.RedirectUri;

        var listenerPrefix = redirectUri.GetLeftPart(UriPartial.Authority);
        if (!listenerPrefix.EndsWith('/')) listenerPrefix += "/";

        var listener = new HttpListener();
        using var closer = new ListenerCloser(listener, logger);
        listener.Prefixes.Add(listenerPrefix);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex,
                "MCP server '{Name}': could not start the local OAuth callback listener on {Prefix} " +
                "(port may already be in use — set a different McpServers[].OAuth.CallbackPort).",
                serverName, listenerPrefix);
            return null;
        }

        logger?.LogInformation(
            "MCP server '{Name}' requires authorization. Opening your browser — if it doesn't open, " +
            "visit this URL manually:\n  {Url}", serverName, authorizationUrl);

        OpenBrowser(authorizationUrl);

        // GetContextAsync has no CancellationToken overload — Stop() is what unblocks it.
        using var registration = cancellationToken.Register(() =>
        {
            try { listener.Stop(); } catch { /* already stopped/disposed */ }
        });

        try
        {
            var httpContext = await listener.GetContextAsync();
            var query = HttpUtility.ParseQueryString(httpContext.Request.Url?.Query ?? string.Empty);
            var code  = query["code"];
            var state = query["state"];
            var iss   = query["iss"];
            var error = query["error"];

            var (title, body) = string.IsNullOrEmpty(error)
                ? ("Authorization complete", "You can close this window and return to fuseraft.")
                : ("Authorization failed", HttpUtility.HtmlEncode(error));
            WriteHtmlResponse(httpContext.Response, title, body);

            if (!string.IsNullOrEmpty(error))
            {
                logger?.LogWarning("MCP server '{Name}': authorization denied or failed ({Error}).", serverName, error);
                return null;
            }

            if (string.IsNullOrEmpty(code))
            {
                logger?.LogWarning("MCP server '{Name}': no authorization code received from callback.", serverName);
                return null;
            }

            logger?.LogInformation("MCP server '{Name}': authorization code received.", serverName);
            return new AuthorizationResult { Code = code, State = state, Iss = iss };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("OAuth callback listener cancelled.", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "MCP server '{Name}': error waiting for the OAuth callback.", serverName);
            return null;
        }
        // No finally { listener.Stop() }: teardown is ListenerCloser's job, and it must not be able to
        // throw over the result computed above.
    }

    /// <summary>
    /// Stops and closes the callback listener, swallowing any failure. By the time this runs the
    /// login outcome (a code, a denial, a cancellation) is already decided, so a teardown error
    /// must never replace it. .NET's managed <see cref="HttpListener"/> can throw
    /// "Address already in use" from <c>Stop()</c>/<c>Close()</c>: unregistering a prefix
    /// re-creates the endpoint it just released, and that bind fails if anything took the port in
    /// the meantime (or its just-served connection is still in TIME_WAIT). Left unhandled that
    /// turned a successful authorization into a failed login right after the user clicked Allow.
    /// </summary>
    internal sealed class ListenerCloser(HttpListener listener, ILogger? logger = null) : IDisposable
    {
        public void Dispose()
        {
            Try(() => { if (listener.IsListening) listener.Stop(); });
            Try(listener.Close);
        }

        private void Try(Action teardown)
        {
            try { teardown(); }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "OAuth callback listener teardown failed (ignored).");
            }
        }
    }

    private static void WriteHtmlResponse(HttpListenerResponse response, string title, string body)
    {
        var html = $"<html><body><h1>{title}</h1><p>{body}</p></body></html>";
        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = buffer.Length;
        response.ContentType     = "text/html";
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    private void OpenBrowser(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            logger?.LogWarning("MCP server '{Name}': refusing to open a non-http(s) authorization URL.", serverName);
            return;
        }

        // Testability seam — real browser-launch has a visible side effect (a window opening
        // on the user's desktop) that unit tests must not trigger.
        if (openBrowser is not null)
        {
            openBrowser(url);
            return;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{url}\"") { UseShellExecute = false });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start(new ProcessStartInfo("open", $"\"{url}\"") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "MCP server '{Name}': could not launch a browser automatically — open this URL manually:\n  {Url}",
                serverName, url);
        }
    }
}
