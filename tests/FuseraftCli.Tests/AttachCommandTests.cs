using fuseraft.Cli.Commands;

namespace FuseraftCli.Tests;

public sealed class AttachCommandTests
{
    [Fact]
    public void PickedUp_IsTheRunningTaskAHelloNames()
    {
        Assert.Equal("abc12345", AttachCommand.PickedUp(
            """{"type":"hello","clientId":"me","inFlight":{"sessionId":"abc12345","status":"running"},"pendingResults":[]}"""));
    }

    [Theory]
    [InlineData("""{"type":"hello","inFlight":null}""")]
    [InlineData("""{"type":"hello"}""")]
    [InlineData("""{"type":"result","inFlight":{"sessionId":"abc12345"}}""")]
    [InlineData("not json")]
    public void PickedUp_IsNullWithoutARunningTask(string line) => Assert.Null(AttachCommand.PickedUp(line));

    [Fact]
    public void IsTlsFailure_FindsTheCertificateErrorUnderTheWrappers()
    {
        var wrapped = new System.Net.WebSockets.WebSocketException("Unable to connect to the remote server",
            new HttpRequestException("SSL failed", new System.Security.Authentication.AuthenticationException("bad cert")));

        Assert.True(AttachCommand.IsTlsFailure(wrapped));
        Assert.False(AttachCommand.IsTlsFailure(new System.Net.WebSockets.WebSocketException("401")));
    }

    [Theory]
    [InlineData("""{"type":"hello","readOnly":true}""", true)]
    [InlineData("""{"type":"hello","readOnly":false}""", false)]
    [InlineData("""{"type":"hello"}""", false)]
    public void IsReadOnly_ReadsTheHello(string hello, bool expected) => Assert.Equal(expected, AttachCommand.IsReadOnly(hello));

    [Theory]
    [InlineData(null, "AB", null, "wss://")]
    [InlineData("ws://h:1/attach", "AB", null, "wss://")]
    [InlineData("wss://h:1/attach", "not-hex", null, "64 hex")]
    [InlineData(null, null, "me", "needs --url")]
    [InlineData("ws://h:1/attach", null, "bad id!", "1-64")]
    public void Validate_RefusesFlagsThatDontGoTogether(string? url, string? pin, string? clientId, string expected)
    {
        var settings = new AttachSettings { Url = url, CertSha256 = pin, ClientId = clientId };

        var result = settings.Validate();

        Assert.False(result.Successful);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public void Validate_AcceptsAPinnedClient()
    {
        var settings = new AttachSettings
        {
            Url = "wss://h:1/attach", CertSha256 = new string('a', 64), ClientId = "laptop",
        };

        Assert.True(settings.Validate().Successful);
    }
}
