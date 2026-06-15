using System.Net;
using System.Net.Sockets;

namespace Bmt.Preflight.Net;

/// <summary>
/// Classifies an IP address as private (RFC1918 / loopback / link-local / CGNAT / IPv6 ULA) vs.
/// public, for preflight check 3 (network path must be private, not the public internet).
/// </summary>
internal static class IpClassifier
{
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes(); // big-endian
            return b[0] switch
            {
                10 => true,                                  // 10.0.0.0/8
                127 => true,                                 // loopback
                169 when b[1] == 254 => true,                // 169.254.0.0/16 link-local
                172 when b[1] >= 16 && b[1] <= 31 => true,   // 172.16.0.0/12
                192 when b[1] == 168 => true,                // 192.168.0.0/16
                100 when b[1] >= 64 && b[1] <= 127 => true,  // 100.64.0.0/10 CGNAT
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            {
                return true;
            }

            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // fc00::/7 unique-local
        }

        return false;
    }

    /// <summary>Resolve an endpoint host to IP addresses (or return the literal IP for an IPEndPoint).</summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(EndPoint endPoint, CancellationToken ct)
    {
        switch (endPoint)
        {
            case IPEndPoint ip:
                return new[] { ip.Address };
            case DnsEndPoint dns:
                if (IPAddress.TryParse(dns.Host, out var literal))
                {
                    return new[] { literal };
                }

                var entries = await Dns.GetHostAddressesAsync(dns.Host, ct).ConfigureAwait(false);
                return entries;
            default:
                return Array.Empty<IPAddress>();
        }
    }

    public static string HostOf(EndPoint endPoint) => endPoint switch
    {
        IPEndPoint ip => ip.Address.ToString(),
        DnsEndPoint dns => dns.Host,
        _ => endPoint.ToString() ?? "?",
    };
}
