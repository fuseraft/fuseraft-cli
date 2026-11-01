using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;

namespace fuseraft.Cli.Serve;

/// <summary>The certificate <c>fuseraft serve --tls-cert</c> serves its HTTP endpoints with.</summary>
public static class ServeTls
{
    public const string PasswordVariable = "FUSERAFT_SERVE_TLS_PASSWORD";

    /// <summary>
    /// Loads a PEM certificate (with its key in <paramref name="keyPath"/> or the same file) or a PKCS#12 file
    /// (<c>.pfx</c>/<c>.p12</c>, password from <see cref="PasswordVariable"/>). Throws with a readable message.
    /// </summary>
    public static X509Certificate2 Load(string certPath, string? keyPath, string? password)
    {
        if (!File.Exists(certPath)) throw new InvalidOperationException($"--tls-cert {certPath} doesn't exist.");
        if (keyPath is not null && !File.Exists(keyPath)) throw new InvalidOperationException($"--tls-key {keyPath} doesn't exist.");

        X509Certificate2 cert;
        try
        {
            if (Path.GetExtension(certPath).ToLowerInvariant() is ".pfx" or ".p12")
            {
                if (keyPath is not null) throw new InvalidOperationException("--tls-key is only for a PEM certificate; a .pfx holds its own key.");
                cert = X509CertificateLoader.LoadPkcs12FromFile(certPath, password);
            }
            else
            {
                // A key loaded from PEM is ephemeral, which SslStream can't use on Windows; a PKCS#12 round trip fixes that.
                using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                cert = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            }
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"Couldn't load the TLS certificate {certPath}: {ex.Message}", ex);
        }

        if (!cert.HasPrivateKey)
            throw new InvalidOperationException($"{certPath} has no private key. Pass it with --tls-key, or use a .pfx that includes it.");
        if (cert.NotAfter < DateTime.Now)
            throw new InvalidOperationException($"The TLS certificate {certPath} expired on {cert.NotAfter:yyyy-MM-dd}.");
        return cert;
    }

    /// <summary>The SHA-256 fingerprint a client pins a self-signed certificate by, as <c>AB:CD:…</c>.</summary>
    public static string Fingerprint(X509Certificate2 cert) =>
        string.Join(':', cert.GetCertHashString(HashAlgorithmName.SHA256).Chunk(2).Select(c => new string(c)));

    /// <summary>A SHA-256 fingerprint as 64 upper-case hex digits, from any of the usual spellings; null when it isn't one.</summary>
    public static string? NormalizeFingerprint(string fingerprint)
    {
        var hex = new string([.. fingerprint.Where(c => c is not (':' or ' ' or '-'))]).ToUpperInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>
    /// Accepts the server's certificate when its SHA-256 fingerprint is <paramref name="pin"/>, whoever issued it,
    /// and refuses any other.
    /// </summary>
    public static System.Net.Security.RemoteCertificateValidationCallback Pinned(string pin) =>
        (_, cert, _, _) => cert is not null &&
            Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).Equals(pin, StringComparison.OrdinalIgnoreCase);

    /// <summary>Listens on <paramref name="host"/>:<paramref name="port"/>, over TLS when there's a certificate.</summary>
    internal static void Listen(IWebHostBuilder webHost, string host, int port, X509Certificate2? cert)
    {
        if (cert is null)
        {
            webHost.UseUrls($"http://{host}:{port}");
            return;
        }
        webHost.UseUrls($"https://{host}:{port}");
        // The slim builder leaves out Kestrel's HTTPS support until asked for it.
        webHost.UseKestrelHttpsConfiguration();
        webHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(https => https.ServerCertificate = cert));
    }
}
