using System.Net;
using System.Net.Sockets;

namespace fuseraft.Core;

/// <summary>Whether a provider URL points at this machine or the local network, where servers like Ollama run without an API key.</summary>
public static class LocalEndpoint
{
    private static readonly string[] LocalSuffixes = [".local", ".lan", ".home.arpa", ".internal"];

    public static bool IsLocal(string? endpoint)
    {
        endpoint = endpoint?.Trim() ?? "";
        // The terminal wizard accepts "192.168.1.5:11434" without a scheme.
        if (endpoint.Length > 0 && !endpoint.Contains("://")) endpoint = "http://" + endpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown)
            return false;
        if (uri.IsLoopback) return true;

        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var ip))
            return IsPrivate(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

        var host = uri.IdnHost.TrimEnd('.');
        return !host.Contains('.')
               || LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            // fc00::/7 unique local addresses, fe80::/10 link-local.
            return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC || ip.IsIPv6LinkLocal;

        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               // 100.64.0.0/10: carrier-grade NAT, which Tailscale uses for its devices.
               || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}
