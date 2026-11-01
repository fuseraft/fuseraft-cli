using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using fuseraft.Cli.Serve;

namespace FuseraftCli.Tests;

public sealed class ServeTlsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-tls-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_ReadsAPemCertificateAndItsKey()
    {
        var (certPath, keyPath) = WritePem(DateTimeOffset.UtcNow.AddDays(30));

        using var cert = ServeTls.Load(certPath, keyPath, null);

        Assert.True(cert.HasPrivateKey);
        Assert.Equal("CN=fuseraft-test", cert.Subject);
    }

    [Fact]
    public void Load_ReadsAPfxWithItsPassword()
    {
        using var source = Create(DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(_dir, "serve.pfx");
        File.WriteAllBytes(path, source.Export(X509ContentType.Pkcs12, "pw"));

        using var cert = ServeTls.Load(path, null, "pw");

        Assert.True(cert.HasPrivateKey);
    }

    [Fact]
    public void Load_RefusesACertificateWithoutItsKey()
    {
        var (certPath, _) = WritePem(DateTimeOffset.UtcNow.AddDays(30));

        var ex = Assert.Throws<InvalidOperationException>(() => ServeTls.Load(certPath, null, null));

        Assert.Contains("TLS certificate", ex.Message);
    }

    [Fact]
    public void Load_RefusesAnExpiredCertificate()
    {
        var (certPath, keyPath) = WritePem(DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Contains("expired", Assert.Throws<InvalidOperationException>(() => ServeTls.Load(certPath, keyPath, null)).Message);
    }

    [Fact]
    public void Fingerprint_IsColonSeparatedSha256()
    {
        using var cert = Create(DateTimeOffset.UtcNow.AddDays(30));

        var fingerprint = ServeTls.Fingerprint(cert);

        Assert.Equal(32, fingerprint.Split(':').Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(cert.RawData)), fingerprint.Replace(":", ""));
    }

    [Fact]
    public async Task Attach_OverTls_SpeaksTheAttachProtocol()
    {
        var (certPath, keyPath) = WritePem(DateTimeOffset.UtcNow.AddDays(30));
        var cert = ServeTls.Load(certPath, keyPath, null);
        var queue = new ServeTaskQueue();
        var listener = new ServeSocketListener("/unused.sock", queue, new ServeHumanApprovalService(false),
            NullLogger<ServeSocketListener>.Instance);
        var options = new ServeHttpOptions("127.0.0.1", 0, null, cert);
        await using var app = ServeHttpHost.Build(options, queue, listener.HandleStreamAsync);
        await app.StartAsync();
        var url = app.Urls.First().TrimEnd('/');
        Assert.StartsWith("https://", url);

        using var ws = new ClientWebSocket();
        var presented = "";
        ws.Options.RemoteCertificateValidationCallback = (_, c, _, _) =>
        {
            presented = ServeTls.Fingerprint(new X509Certificate2(c!));
            return true;
        };
        await ws.ConnectAsync(new Uri(url.Replace("https://", "wss://") + ServeHttpHost.AttachPath), default);

        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("hello", Encoding.UTF8.GetString(buffer, 0, result.Count));
        Assert.Equal(ServeTls.Fingerprint(cert), presented);
    }

    [Theory]
    [InlineData("ab:cd")]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000")]
    public void NormalizeFingerprint_RefusesWhatIsntOne(string fingerprint) =>
        Assert.Null(ServeTls.NormalizeFingerprint(fingerprint));

    [Fact]
    public void NormalizeFingerprint_AcceptsColonsSpacesAndLowerCase()
    {
        using var cert = Create(DateTimeOffset.UtcNow.AddDays(30));
        var printed = ServeTls.Fingerprint(cert);

        Assert.Equal(printed.Replace(":", ""), ServeTls.NormalizeFingerprint(printed.ToLowerInvariant().Replace(':', ' ')));
    }

    [Fact]
    public async Task Pinned_ConnectsToTheCertificateWithThatFingerprint_AndRefusesAnother()
    {
        var (certPath, keyPath) = WritePem(DateTimeOffset.UtcNow.AddDays(30));
        var cert = ServeTls.Load(certPath, keyPath, null);
        var queue = new ServeTaskQueue();
        var listener = new ServeSocketListener("/unused.sock", queue, new ServeHumanApprovalService(false),
            NullLogger<ServeSocketListener>.Instance);
        await using var app = ServeHttpHost.Build(new ServeHttpOptions("127.0.0.1", 0, null, cert), queue, listener.HandleStreamAsync);
        await app.StartAsync();
        var uri = new Uri(app.Urls.First().Replace("https://", "wss://") + ServeHttpHost.AttachPath);

        using var pinned = new ClientWebSocket();
        pinned.Options.RemoteCertificateValidationCallback = ServeTls.Pinned(ServeTls.NormalizeFingerprint(ServeTls.Fingerprint(cert))!);
        await pinned.ConnectAsync(uri, default);

        using var other = Create(DateTimeOffset.UtcNow.AddDays(30));
        using var wrong = new ClientWebSocket();
        wrong.Options.RemoteCertificateValidationCallback = ServeTls.Pinned(ServeTls.NormalizeFingerprint(ServeTls.Fingerprint(other))!);
        await Assert.ThrowsAsync<WebSocketException>(() => wrong.ConnectAsync(uri, default));
    }

    private (string Cert, string Key) WritePem(DateTimeOffset notAfter)
    {
        using var cert = Create(notAfter);
        var certPath = Path.Combine(_dir, $"{Guid.NewGuid():N}.crt");
        var keyPath  = Path.Combine(_dir, $"{Guid.NewGuid():N}.key");
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, cert.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath);
    }

    private static X509Certificate2 Create(DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=fuseraft-test", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(notAfter.AddDays(-30), notAfter);
    }
}
