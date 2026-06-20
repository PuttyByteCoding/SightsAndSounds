using System.Net;
using System.Net.Sockets;

namespace VideoOrganizer.API.Access;

/// <summary>
/// Parses and matches client IPs against an allowlist of single addresses and
/// CIDR ranges (#124 — "limit access by device IP"). Pure logic so it can be
/// unit-tested; the middleware that uses it lives in <see cref="IpAccessSetup"/>.
///
/// Entries may be a bare address (<c>127.0.0.1</c>, <c>192.168.1.50</c>,
/// <c>::1</c>) — treated as a single host — or CIDR (<c>192.168.1.0/24</c>,
/// <c>10.0.0.0/8</c>). IPv4-mapped IPv6 client addresses (<c>::ffff:192.168.1.5</c>,
/// how Kestrel often reports IPv4 peers) are unwrapped before matching.
/// </summary>
public static class IpAllowList
{
    /// <summary>Parses allowlist entries; invalid entries are skipped.</summary>
    public static List<IPNetwork> Parse(IEnumerable<string> entries)
    {
        var networks = new List<IPNetwork>();
        foreach (var raw in entries)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry)) continue;
            try
            {
                if (entry.Contains('/'))
                {
                    networks.Add(IPNetwork.Parse(entry));
                }
                else if (IPAddress.TryParse(entry, out var ip))
                {
                    var bits = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                    networks.Add(new IPNetwork(ip, bits));
                }
            }
            catch
            {
                // Skip a malformed entry rather than failing the whole list.
            }
        }
        return networks;
    }

    /// <summary>True if <paramref name="ip"/> falls in any allowed network.</summary>
    public static bool IsAllowed(IPAddress? ip, IReadOnlyList<IPNetwork> allowed)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        foreach (var net in allowed)
        {
            if (net.BaseAddress.AddressFamily == ip.AddressFamily && net.Contains(ip))
                return true;
        }
        return false;
    }
}
