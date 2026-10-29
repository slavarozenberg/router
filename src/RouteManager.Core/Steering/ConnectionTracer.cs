using System.Net;
using System.Net.NetworkInformation;
using RouteManager.Core.Net;

namespace RouteManager.Core.Steering;

/// <summary>A connection with the route it takes. <see cref="RuleId"/> is set when that route comes from a rule.</summary>
public sealed record TracedConnection(TcpConnection Connection, RouteKey? Route, Guid? RuleId);

public static class ConnectionTracer
{
    /// <summary>
    /// Resolves the route of every live outbound-capable connection (listeners, loopback and closing
    /// sockets are skipped) and matches it against the planned routes to tell which rule steers it.
    /// </summary>
    public static IReadOnlyList<TracedConnection> Trace(IConnectionTable table, IReadOnlyList<PlannedRoute> planned)
    {
        var ruleByRoute = new Dictionary<RouteKey, Guid>();
        foreach (var route in planned)
            ruleByRoute.TryAdd(route.Key, route.RuleId);

        var resolved = new Dictionary<(IPAddress, IPAddress), RouteKey?>();
        var result = new List<TracedConnection>();
        foreach (var connection in table.GetTcpConnections())
        {
            if (!IsShown(connection))
                continue;
            var pair = (connection.Local.Address, connection.Remote.Address);
            if (!resolved.TryGetValue(pair, out var key))
                resolved[pair] = key = table.GetBestRoute(pair.Item1, pair.Item2);
            Guid? ruleId = key is not null && ruleByRoute.TryGetValue(key, out var id) ? id : null;
            result.Add(new TracedConnection(connection, key, ruleId));
        }
        return result;
    }

    private static bool IsShown(TcpConnection connection) =>
        connection.State is not (TcpState.Listen or TcpState.TimeWait or TcpState.Closed or TcpState.DeleteTcb)
        && !IPAddress.IsLoopback(connection.Remote.Address)
        && !connection.Remote.Address.Equals(IPAddress.Any)
        && !connection.Remote.Address.Equals(IPAddress.IPv6Any);
}
