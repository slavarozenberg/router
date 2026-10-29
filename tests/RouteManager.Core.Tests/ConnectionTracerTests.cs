using System.Net;
using System.Net.NetworkInformation;
using RouteManager.Core.Net;
using RouteManager.Core.Steering;

namespace RouteManager.Core.Tests;

public class ConnectionTracerTests
{
    private static readonly RouteKey Default = new(Nic.LanLuid, IpPrefix.Parse("0.0.0.0/0"), IPAddress.Parse("172.19.4.1"));
    private static readonly RouteKey Steered = new(Nic.VpnLuid, IpPrefix.Parse("10.90.0.0/22"), IPAddress.Any);

    private static TcpConnection Tcp(string remote, TcpState state = TcpState.Established, int pid = 42) =>
        new(pid, IPEndPoint.Parse("172.19.4.20:50000"), IPEndPoint.Parse(remote), state);

    [Fact]
    public void Connection_on_a_planned_route_is_attributed_to_its_rule()
    {
        var ruleId = Guid.NewGuid();
        var table = new FakeConnectionTable([Default, Steered], Tcp("10.90.1.5:443"), Tcp("8.8.8.8:443"));

        var traced = ConnectionTracer.Trace(table, [new PlannedRoute(Steered, 5, ruleId)]);

        Assert.Collection(traced,
            t => Assert.Equal((Steered, ruleId), (t.Route!, t.RuleId!.Value)),
            t => Assert.Equal((Default, (Guid?)null), (t.Route!, t.RuleId)));
    }

    [Fact]
    public void Listeners_loopback_and_closed_sockets_are_skipped()
    {
        var table = new FakeConnectionTable([Default],
            Tcp("0.0.0.0:0", TcpState.Listen), Tcp("127.0.0.1:9000"), Tcp("8.8.8.8:443", TcpState.TimeWait), Tcp("8.8.8.8:80"));

        var traced = ConnectionTracer.Trace(table, []);

        Assert.Equal(80, Assert.Single(traced).Connection.Remote.Port);
    }

    [Fact]
    public void Same_address_pair_is_resolved_once()
    {
        var table = new FakeConnectionTable([Default], Tcp("8.8.8.8:443", pid: 1), Tcp("8.8.8.8:443", pid: 2));

        ConnectionTracer.Trace(table, []);

        Assert.Equal(1, table.Lookups);
    }

    [Fact]
    public void Unroutable_destination_has_no_route()
    {
        var table = new FakeConnectionTable([Steered], Tcp("8.8.8.8:443"));

        Assert.Null(Assert.Single(ConnectionTracer.Trace(table, [])).Route);
    }
}
