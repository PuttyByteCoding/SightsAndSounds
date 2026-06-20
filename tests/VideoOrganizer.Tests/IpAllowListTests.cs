using System.Net;
using VideoOrganizer.API.Access;
using Xunit;

namespace VideoOrganizer.Tests;

public class IpAllowListTests
{
    private static bool Allowed(string ip, params string[] allow)
        => IpAllowList.IsAllowed(IPAddress.Parse(ip), IpAllowList.Parse(allow));

    [Fact]
    public void Single_host_matches_exactly()
    {
        Assert.True(Allowed("192.168.1.50", "192.168.1.50"));
        Assert.False(Allowed("192.168.1.51", "192.168.1.50"));
    }

    [Fact]
    public void Cidr_range_matches_members_only()
    {
        Assert.True(Allowed("192.168.1.99", "192.168.1.0/24"));
        Assert.True(Allowed("192.168.1.1", "192.168.1.0/24"));
        Assert.False(Allowed("192.168.2.1", "192.168.1.0/24"));
        Assert.True(Allowed("10.4.5.6", "10.0.0.0/8"));
        Assert.False(Allowed("11.0.0.1", "10.0.0.0/8"));
    }

    [Fact]
    public void Loopback_v4_and_v6()
    {
        Assert.True(Allowed("127.0.0.1", "127.0.0.1/32", "::1/128"));
        Assert.True(Allowed("::1", "127.0.0.1/32", "::1/128"));
    }

    [Fact]
    public void Ipv4_mapped_ipv6_is_unwrapped_before_matching()
    {
        // Kestrel often reports IPv4 peers as ::ffff:a.b.c.d.
        Assert.True(Allowed("::ffff:192.168.1.50", "192.168.1.0/24"));
        Assert.True(Allowed("::ffff:127.0.0.1", "127.0.0.1/32"));
    }

    [Fact]
    public void Empty_or_no_match_denies()
    {
        Assert.False(IpAllowList.IsAllowed(IPAddress.Parse("192.168.1.1"), IpAllowList.Parse([])));
        Assert.False(IpAllowList.IsAllowed(null, IpAllowList.Parse(["127.0.0.1/32"])));
    }

    [Fact]
    public void Malformed_entries_are_skipped_not_thrown()
    {
        var nets = IpAllowList.Parse(["", "  ", "not-an-ip", "999.999.0.0/16", "192.168.1.0/24"]);
        Assert.Single(nets);
        Assert.True(IpAllowList.IsAllowed(IPAddress.Parse("192.168.1.5"), nets));
    }
}
