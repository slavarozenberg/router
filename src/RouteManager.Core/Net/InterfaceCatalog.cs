using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RouteManager.Core.Native;

namespace RouteManager.Core.Net;

/// <summary>Snapshot of one network interface as far as steering cares: can it carry a family, and via which gateway.</summary>
public sealed record InterfaceInfo(
    ulong Luid,
    string Name,
    string Description,
    bool IsUp,
    bool IsLoopback,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<IPAddress> Gateways)
{
    /// <summary>Has a routable source address: link-local (fe80::/10) and APIPA (169.254/16) don't count.</summary>
    public bool HasAddress(AddressFamily family) =>
        Addresses.Any(a => a.AddressFamily == family && !a.IsIPv6LinkLocal && !Apipa.Contains(a));

    private static readonly IpPrefix Apipa = IpPrefix.Parse("169.254.0.0/16");

    /// <summary>The interface is up and has an address of <paramref name="family"/> to send from.</summary>
    public bool CanCarry(AddressFamily family) => IsUp && HasAddress(family);

    /// <summary>Gateway to send <paramref name="family"/> traffic through, or null for on-link (typical for VPN tunnels).</summary>
    public IPAddress? GatewayFor(AddressFamily family) =>
        Gateways.FirstOrDefault(g => g.AddressFamily == family && !g.Equals(IPAddress.Any) && !g.Equals(IPAddress.IPv6Any));
}

public interface IInterfaceCatalog
{
    IReadOnlyList<InterfaceInfo> GetInterfaces();
}

public sealed class WindowsInterfaceCatalog : IInterfaceCatalog
{
    public IReadOnlyList<InterfaceInfo> GetInterfaces()
    {
        var result = new List<InterfaceInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!Guid.TryParse(nic.Id, out var guid) || IpHelper.ConvertInterfaceGuidToLuid(guid, out var luid) != IpHelper.NO_ERROR)
                continue;

            var properties = nic.GetIPProperties();
            result.Add(new InterfaceInfo(
                luid,
                nic.Name,
                nic.Description,
                nic.OperationalStatus == OperationalStatus.Up,
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                properties.UnicastAddresses.Select(a => a.Address).ToList(),
                properties.GatewayAddresses.Select(g => g.Address).ToList()));
        }
        return result.OrderBy(i => i.IsLoopback).ThenByDescending(i => i.IsUp).ThenBy(i => i.Name).ToList();
    }
}
