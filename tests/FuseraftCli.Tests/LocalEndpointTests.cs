using fuseraft.Core;

namespace FuseraftCli.Tests;

public sealed class LocalEndpointTests
{
    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://[::1]:11434")]
    [InlineData("http://192.168.1.102:11434/v1")]
    [InlineData("http://10.0.0.5:11434")]
    [InlineData("http://172.16.4.2:8000/v1")]
    [InlineData("http://172.31.255.1:8000/v1")]
    [InlineData("http://169.254.10.20:11434")]
    [InlineData("http://100.101.102.103:11434")]
    [InlineData("http://[fd12:3456::1]:11434")]
    [InlineData("http://[fe80::1]:11434")]
    [InlineData("http://gpu-box:11434/v1")]
    [InlineData("http://gpu-box.local:11434/v1")]
    [InlineData("http://ollama.lan/v1")]
    [InlineData("http://ollama.home.arpa/v1")]
    [InlineData("192.168.1.5:11434")]
    [InlineData("localhost:11434")]
    public void LocalAndPrivateNetworkHosts_AreLocal(string endpoint) =>
        Assert.True(LocalEndpoint.IsLocal(endpoint));

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.x.ai/v1")]
    [InlineData("https://gateway.example.com/v1")]
    [InlineData("http://8.8.8.8/v1")]
    [InlineData("http://172.32.0.1/v1")]
    [InlineData("http://192.169.1.1/v1")]
    [InlineData("http://100.128.0.1/v1")]
    [InlineData("http://[2001:db8::1]/v1")]
    [InlineData("http://localhost.example.com/v1")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a url")]
    public void PublicOrMissingHosts_AreNotLocal(string? endpoint) =>
        Assert.False(LocalEndpoint.IsLocal(endpoint));
}
