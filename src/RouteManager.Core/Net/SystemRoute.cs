using System.Net;

namespace RouteManager.Core.Net;

/// <summary>
/// Identity of a route in the system table. Windows keys routes by interface, destination and next hop;
/// on-link routes use the unspecified address (0.0.0.0 or ::) as next hop.
/// </summary>
public sealed record RouteKey(ulong InterfaceLuid, IpPrefix Destination, IPAddress NextHop)
{
    public bool IsOnLink => NextHop.Equals(IPAddress.Any) || NextHop.Equals(IPAddress.IPv6Any);

    // IPAddress equality includes the IPv6 scope id, which the routing table does not echo back reliably.
    public bool Equals(RouteKey? other) =>
        other is not null
        && InterfaceLuid == other.InterfaceLuid
        && Destination == other.Destination
        && NextHop.GetAddressBytes().AsSpan().SequenceEqual(other.NextHop.GetAddressBytes());

    public override int GetHashCode() =>
        HashCode.Combine(InterfaceLuid, Destination, Convert.ToHexString(NextHop.GetAddressBytes()));

    public override string ToString() => $"{Destination} via {(IsOnLink ? "on-link" : NextHop)} (if {InterfaceLuid})";
}

/// <summary>A row read from the system routing table.</summary>
public sealed record SystemRoute(RouteKey Key, uint InterfaceIndex, uint Metric, int Protocol);
