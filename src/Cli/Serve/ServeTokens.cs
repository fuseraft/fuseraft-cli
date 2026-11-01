using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace fuseraft.Cli.Serve;

/// <summary>A bearer token the daemon accepts: its name, and whether it may only watch.</summary>
/// <param name="ReadOnly">
/// Sees the daemon's progress and asks <c>get_status</c>/<c>get_result</c>, but can't dispatch a task, so it's never
/// asked to approve one either.
/// </param>
public sealed record ServeToken(string Name, bool ReadOnly);

/// <summary>
/// The bearer tokens <c>fuseraft serve</c>'s HTTP endpoints accept: <see cref="ServeHttpHost.TokenVariable"/>
/// (named <c>default</c>, full access) and the named tokens in <c>--tokens-file</c>, one <c>name token [full|read]</c>
/// per line. The file is re-read when it changes, so a token is added, revoked or rescoped without restarting the daemon.
/// </summary>
public sealed partial class ServeTokens
{
    public const string DefaultName = "default";
    public const int MinFileTokenLength = 16;

    private const string ItemKey    = "fuseraft.token";
    private const string ScopeClaim = "fuseraft:scope";

    private readonly string? _token;
    private readonly string? _path;
    private readonly Lock _lock = new();
    private IReadOnlyList<(ServeToken Token, byte[] Secret)> _fileTokens = [];
    private DateTime _loadedWriteTime;

    /// <summary>Throws <see cref="InvalidOperationException"/> when <paramref name="path"/> can't be used.</summary>
    public ServeTokens(string? token, string? path)
    {
        _token = string.IsNullOrEmpty(token) ? null : token;
        _path  = path;
        if (path is null) return;
        _fileTokens      = Parse(path);
        _loadedWriteTime = File.GetLastWriteTimeUtc(path);
    }

    public bool Any
    {
        get
        {
            if (_token is not null) return true;
            lock (_lock) return _fileTokens.Count > 0;
        }
    }

    /// <summary>The token an <c>Authorization</c> header carries, or null when it carries none this daemon accepts.</summary>
    public ServeToken? Authenticate(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var presented = Encoding.UTF8.GetBytes(authorization["Bearer ".Length..]);
        Refresh();

        ServeToken? match = null;
        // Every token is compared, so the time taken says nothing about which one nearly matched.
        if (_token is not null && CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(_token)))
            match = new ServeToken(DefaultName, ReadOnly: false);
        IReadOnlyList<(ServeToken Token, byte[] Secret)> fileTokens;
        lock (_lock) fileTokens = _fileTokens;
        foreach (var (token, secret) in fileTokens)
            if (CryptographicOperations.FixedTimeEquals(presented, secret)) match ??= token;
        return match;
    }

    /// <summary>
    /// Whether <paramref name="token"/> is still accepted as it was: false once it's removed from the file, or its
    /// scope changed, so a connection made under the old scope doesn't keep it.
    /// </summary>
    public bool IsValid(ServeToken token)
    {
        if (token.Name == DefaultName && _token is not null) return !token.ReadOnly;
        Refresh();
        lock (_lock) return _fileTokens.Any(t => t.Token == token);
    }

    /// <summary>The token the request authenticated with; null when the daemon takes no token.</summary>
    public static ServeToken? Of(HttpContext ctx) => ctx.Items[ItemKey] as ServeToken;

    /// <summary>Whether <paramref name="user"/>, as the MCP server sees a request's caller, holds a read-only token.</summary>
    public static bool IsReadOnly(ClaimsPrincipal? user) => user?.HasClaim(ScopeClaim, "read") == true;

    internal Func<HttpContext, RequestDelegate, Task> Require() => async (ctx, next) =>
    {
        if (Authenticate(ctx.Request.Headers.Authorization.ToString()) is not { } token)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
        ctx.Items[ItemKey] = token;
        // The MCP server reads the caller from the request's user.
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, token.Name),
            new Claim(ScopeClaim, token.ReadOnly ? "read" : "full"),
        ], authenticationType: "Bearer"));
        await next(ctx);
    };

    private void Refresh()
    {
        if (_path is null) return;
        lock (_lock)
        {
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(_path); }
            catch (IOException) { return; }
            if (written == _loadedWriteTime) return;
            try
            {
                _fileTokens      = File.Exists(_path) ? Parse(_path) : [];
                _loadedWriteTime = written;
            }
            // A file caught half-written, or with a mistake in it, keeps the tokens it had.
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        }
    }

    private static List<(ServeToken, byte[])> Parse(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"--tokens-file {path} doesn't exist.");
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new InvalidOperationException($"--tokens-file {path} can be read by other users; run chmod 600 on it.");
        List<(ServeToken, byte[])> tokens = [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is not (2 or 3))
                throw new InvalidOperationException($"{path}:{lineNumber}: expected 'name token [full|read]', got {parts.Length} fields.");
            var (name, secret) = (parts[0], parts[1]);
            if (!ValidName().IsMatch(name))
                throw new InvalidOperationException($"{path}:{lineNumber}: a token name is 1-64 letters, digits, '-' or '_'.");
            if (name == DefaultName)
                throw new InvalidOperationException($"{path}:{lineNumber}: '{DefaultName}' is the name of {ServeHttpHost.TokenVariable}; pick another.");
            if (!names.Add(name))
                throw new InvalidOperationException($"{path}:{lineNumber}: the name '{name}' is already used.");
            if (secret.Length < MinFileTokenLength)
                throw new InvalidOperationException($"{path}:{lineNumber}: the token for '{name}' is shorter than {MinFileTokenLength} characters.");
            var readOnly = parts.Length == 3 ? parts[2] switch
            {
                "full" => false,
                "read" => true,
                var other => throw new InvalidOperationException($"{path}:{lineNumber}: the scope for '{name}' is 'full' or 'read', not '{other}'."),
            } : false;
            tokens.Add((new ServeToken(name, readOnly), Encoding.UTF8.GetBytes(secret)));
        }
        return tokens;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidName();
}
