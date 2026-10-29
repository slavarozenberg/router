using System.Net;
using RouteManager.Core.Model;
using RouteManager.Core.Net;

namespace RouteManager.Core.Tests;

internal sealed class FakeRouteTable : IRouteTable
{
    public HashSet<RouteKey> Routes { get; } = [];

    public IReadOnlyList<SystemRoute> GetRoutes() => Routes.Select(k => new SystemRoute(k, 0, 0, 3)).ToList();

    public bool Add(RouteKey key, uint metric) => Routes.Add(key);

    public bool Delete(RouteKey key) => Routes.Remove(key);
}

internal sealed class FakeCatalog(params InterfaceInfo[] interfaces) : IInterfaceCatalog
{
    public List<InterfaceInfo> Interfaces { get; } = [.. interfaces];

    public IReadOnlyList<InterfaceInfo> GetInterfaces() => Interfaces;
}

internal static class Nic
{
    public const ulong VpnLuid = 14918723538255872;
    public const ulong LanLuid = 1689399716741120;

    public static InterfaceInfo Vpn(bool up = true) =>
        new(VpnLuid, "OpenVPN", "tunnel", up, false, [IPAddress.Parse("10.8.0.2")], []);

    public static InterfaceInfo Lan(bool up = true) =>
        new(LanLuid, "Ethernet", "lan", up, false,
            [IPAddress.Parse("172.19.4.20"), IPAddress.Parse("2001:db8::20")],
            [IPAddress.Parse("172.19.4.1")]);

    public static InterfaceRef Ref(InterfaceInfo nic) => new(nic.Luid, nic.Name);

    public static RuleSet RuleSet(params Rule[] rules) => new()
    {
        SteeringEnabled = true,
        Groups = [new RuleGroup { Name = "g", Rules = [.. rules] }],
    };
}
