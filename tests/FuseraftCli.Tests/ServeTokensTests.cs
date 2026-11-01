using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using fuseraft.Cli.Serve;

namespace FuseraftCli.Tests;

public sealed class ServeTokensTests : IAsyncLifetime
{
    private const string Alice = "alice-token-0123456789";
    private const string Bob   = "bob-token-0123456789ab";

    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-tokens-").FullName;
    private WebApplication? _app;

    private string TokensPath => Path.Combine(_dir, "tokens");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Authenticate_NamesTheTokenPresented()
    {
        WriteTokens($"alice {Alice}", $"bob {Bob}");
        var tokens = new ServeTokens("env-token", TokensPath);

        Assert.Equal(new ServeToken("bob", false), tokens.Authenticate("Bearer " + Bob));
        Assert.Equal(new ServeToken(ServeTokens.DefaultName, false), tokens.Authenticate("Bearer env-token"));
        Assert.Null(tokens.Authenticate("Bearer nope"));
        Assert.Null(tokens.Authenticate(Alice));
    }

    [Fact]
    public void AnEditedFile_IsReadAgain()
    {
        WriteTokens($"alice {Alice}");
        var tokens = new ServeTokens(null, TokensPath);

        WriteTokens($"bob {Bob}", later: true);

        Assert.Null(tokens.Authenticate("Bearer " + Alice));
        Assert.Equal("bob", tokens.Authenticate("Bearer " + Bob)?.Name);
        Assert.False(tokens.IsValid(new ServeToken("alice", false)));
    }

    [Fact]
    public void ABrokenEdit_KeepsTheTokensThereWere()
    {
        WriteTokens($"alice {Alice}");
        var tokens = new ServeTokens(null, TokensPath);

        WriteTokens("alice", later: true);

        Assert.Equal("alice", tokens.Authenticate("Bearer " + Alice)?.Name);
    }

    [Theory]
    [InlineData("alice short", "shorter than")]
    [InlineData("al!ce alice-token-0123456789", "token name")]
    [InlineData("default alice-token-0123456789", "pick another")]
    [InlineData("alice alice-token-0123456789\nalice bob-token-0123456789ab", "already used")]
    [InlineData("alice alice-token-0123456789 admin", "'full' or 'read'")]
    public void AFileWithAMistake_IsRefused(string content, string expected)
    {
        WriteTokens(content.Split('\n'));

        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => new ServeTokens(null, TokensPath)).Message);
    }

    [Fact]
    public void AFileOthersCanRead_IsRefused()
    {
        if (OperatingSystem.IsWindows()) return;
        WriteTokens($"alice {Alice}");
        File.SetUnixFileMode(TokensPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        Assert.Contains("chmod 600", Assert.Throws<InvalidOperationException>(() => new ServeTokens(null, TokensPath)).Message);
    }

    [Fact]
    public async Task ATokenRevokedFromTheFile_ClosesItsConnections()
    {
        WriteTokens($"alice {Alice}", $"bob {Bob}");
        await StartAsync();
        using var alice = await ConnectAsync(Alice, "web-1");
        using var bob   = await ConnectAsync(Bob, "web-1");

        WriteTokens($"bob {Bob}", later: true);

        Assert.True(await ClosesAsync(alice));
        Assert.False(await ClosesAsync(bob, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task TheSameClientIdUnderAnotherToken_IsAnotherClient()
    {
        WriteTokens($"alice {Alice}", $"bob {Bob}");
        await StartAsync();
        using var alice = await ConnectAsync(Alice, "web-1");

        using var bob = await ConnectAsync(Bob, "web-1");

        // Bob's connection would have replaced, and closed, Alice's had they been the same client.
        Assert.False(await ClosesAsync(alice, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AReadScope_MakesATokenReadOnly()
    {
        WriteTokens($"alice {Alice} full", $"bob {Bob} read");
        var tokens = new ServeTokens(null, TokensPath);

        Assert.False(tokens.Authenticate("Bearer " + Alice)!.ReadOnly);
        Assert.True(tokens.Authenticate("Bearer " + Bob)!.ReadOnly);
    }

    [Fact]
    public async Task AReadOnlyClient_WatchesButCantSendTasks()
    {
        WriteTokens($"alice {Alice}", $"bob {Bob} read");
        var queue = await StartAsync();
        using var bob = await ConnectAsync(Bob, "watcher", expectReadOnly: true);

        await bob.SendAsync(Encoding.UTF8.GetBytes("""{"type":"user_input","text":"rm -rf /"}"""), WebSocketMessageType.Text, true, default);

        Assert.Contains("read-only", await ReceiveAsync(bob));
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ChangingATokensScope_ClosesItsConnections()
    {
        WriteTokens($"alice {Alice}", $"bob {Bob}");
        await StartAsync();
        using var bob = await ConnectAsync(Bob, "web-1");

        WriteTokens([$"alice {Alice}", $"bob {Bob} read"], later: true);

        Assert.True(await ClosesAsync(bob));
    }

    [Fact]
    public async Task OverMcp_AReadOnlyTokenChecksOnTasksButCantDispatch()
    {
        WriteTokens($"alice {Alice}", $"bob {Bob} read");
        var queue = await StartAsync();
        var id = queue.Enqueue("already queued");
        await using var alice = await McpAsync(Alice);
        await using var bob = await McpAsync(Bob);

        Assert.Contains("dispatch_task", (await alice.ListToolsAsync()).Select(t => t.Name));
        Assert.DoesNotContain("dispatch_task", (await bob.ListToolsAsync()).Select(t => t.Name));
        var refused = await bob.CallToolAsync("dispatch_task", new Dictionary<string, object?> { ["task"] = "rm -rf /" });
        Assert.True(refused.IsError);
        var status = await bob.CallToolAsync("get_status", new Dictionary<string, object?> { ["sessionId"] = id });
        Assert.Contains("queued", ((TextContentBlock)status.Content[0]).Text);
        Assert.Single(queue.Unfinished);
    }

    [Fact]
    public void Validate_AcceptsATokensFileInsteadOfTheVariable()
    {
        WriteTokens($"alice {Alice}");

        Assert.Null(ServeHttpHost.Validate(new ServeHttpOptions("0.0.0.0", 5000, null, TokensFile: TokensPath)));
    }

    private void WriteTokens(params string[] lines) => WriteTokens(lines, later: false);

    private void WriteTokens(string line, bool later) => WriteTokens([line], later);

    private void WriteTokens(string[] lines, bool later)
    {
        File.WriteAllLines(TokensPath, lines);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(TokensPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // Write times are what tell the daemon to read the file again; make sure this one differs.
        if (later) File.SetLastWriteTimeUtc(TokensPath, DateTime.UtcNow.AddSeconds(5));
    }

    private async Task<ServeTaskQueue> StartAsync()
    {
        ServeHttpHost.RevocationCheckInterval = TimeSpan.FromMilliseconds(100);
        var queue = new ServeTaskQueue();
        var listener = new ServeSocketListener("/unused.sock", queue, new ServeHumanApprovalService(false),
            NullLogger<ServeSocketListener>.Instance);
        _app = ServeHttpHost.Build(new ServeHttpOptions("127.0.0.1", 0, null, TokensFile: TokensPath), queue, listener.HandleStreamAsync);
        await _app.StartAsync();
        return queue;
    }

    private async Task<McpClient> McpAsync(string token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint          = new Uri(_app!.Urls.First().TrimEnd('/') + "/mcp"),
            TransportMode     = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        });
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, default).WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private async Task<ClientWebSocket> ConnectAsync(string token, string clientId, bool expectReadOnly = false)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
        ws.Options.SetRequestHeader(ServeAttachClients.ClientIdHeader, clientId);
        await ws.ConnectAsync(new Uri(_app!.Urls.First().Replace("http://", "ws://") + ServeHttpHost.AttachPath), default);
        var buffer = new byte[4096];
        var hello = await ws.ReceiveAsync(buffer, default).WaitAsync(TimeSpan.FromSeconds(10));
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, hello.Count));
        Assert.Equal(clientId, doc.RootElement.GetProperty("clientId").GetString());
        Assert.Equal(expectReadOnly, doc.RootElement.GetProperty("readOnly").GetBoolean());
        return ws;
    }

    /// <summary>Reads until the server closes or drops the connection; false when it's still open after <paramref name="within"/>.</summary>
    private static async Task<bool> ClosesAsync(ClientWebSocket ws, TimeSpan? within = null)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, default).WaitAsync(within ?? TimeSpan.FromSeconds(10));
                if (result.MessageType == WebSocketMessageType.Close) return true;
            }
        }
        catch (WebSocketException) { return true; }
        catch (TimeoutException) { return false; }
    }
}
