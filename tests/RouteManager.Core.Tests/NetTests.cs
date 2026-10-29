using System.Net;
using System.Runtime.InteropServices;
using RouteManager.Core.Native;
using RouteManager.Core.Net;

namespace RouteManager.Core.Tests;

public class IpPrefixTests
{
    [Theory]
    [InlineData("10.90.0.0/22", "10.90.0.0/22")]
    [InlineData("10.90.1.7/22", "10.90.0.0/22")]
    [InlineData("10.0.0.0/255.255.0.0", "10.0.0.0/16")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("fd00:1:2::5/48", "fd00:1:2::/48")]
    public void Parse_normalizes(string text, string expected) =>
        Assert.Equal(expected, IpPrefix.Parse(text).ToString());

    [Theory]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/255.0.255.0")]
    [InlineData("10.0.0.0")]
    [InlineData("nope/8")]
    [InlineData("::/129")]
    public void TryParse_rejects_invalid(string text) => Assert.False(IpPrefix.TryParse(text, out _));

    [Fact]
    public void Contains_checks_network_bits() =>
        Assert.True(IpPrefix.Parse("172.20.2.0/24").Contains(IPAddress.Parse("172.20.2.200")));
}

public class AddressMatchTests
{
    [Theory]
    [InlineData("172.19.4.185", AddressMatchKind.Address)]
    [InlineData("10.80.90.0 - 10.80.90.255", AddressMatchKind.Range)]
    [InlineData("10.90.0.0/22", AddressMatchKind.Subnet)]
    [InlineData("2001:db8::1", AddressMatchKind.Address)]
    public void Parse_detects_kind(string text, AddressMatchKind kind) =>
        Assert.Equal(kind, AddressMatch.Parse(text).Kind);

    [Theory]
    [InlineData("10.0.0.9-10.0.0.1")]
    [InlineData("10.0.0.1-2001:db8::1")]
    [InlineData("example.com")]
    [InlineData("")]
    public void TryParse_rejects_invalid(string text)
    {
        Assert.False(AddressMatch.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("10.80.90.0-10.80.90.255", "10.80.90.0/24")]
    [InlineData("10.0.0.1-10.0.0.6", "10.0.0.1/32 10.0.0.2/31 10.0.0.4/31 10.0.0.6/32")]
    [InlineData("10.0.0.0-10.0.1.255", "10.0.0.0/23")]
    [InlineData("0.0.0.0-255.255.255.255", "0.0.0.0/0")]
    [InlineData("255.255.255.254-255.255.255.255", "255.255.255.254/31")]
    [InlineData("::-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "::/0")]
    [InlineData("2001:db8::-2001:db8::2", "2001:db8::/127 2001:db8::2/128")]
    public void Range_splits_into_minimal_cidrs(string range, string expected) =>
        Assert.Equal(expected, string.Join(" ", AddressMatch.Parse(range).ToPrefixes()));

    [Fact]
    public void Address_becomes_host_prefix() =>
        Assert.Equal("172.19.4.185/32", AddressMatch.Parse("172.19.4.185").ToPrefixes().Single().ToString());
}

public class NativeLayoutTests
{
    [Fact]
    public void Forward_row_matches_netioapi_size()
    {
        Assert.Equal(104, Marshal.SizeOf<IpHelper.MIB_IPFORWARD_ROW2>());
        Assert.Equal(28, Marshal.SizeOf<IpHelper.SOCKADDR_INET>());
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("2001:db8::42")]
    public void Sockaddr_round_trips(string address)
    {
        var ip = IPAddress.Parse(address);
        Assert.Equal(ip, IpHelper.SOCKADDR_INET.From(ip).ToIPAddress());
    }

    [Fact]
    public void Live_routing_table_is_readable()
    {
        var routes = new WindowsRouteTable().GetRoutes();
        Assert.Contains(routes, r => r.Key.Destination.ToString() == "127.0.0.0/8");
    }
}
