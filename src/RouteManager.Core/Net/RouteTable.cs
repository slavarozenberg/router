using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using RouteManager.Core.Native;
using static RouteManager.Core.Native.IpHelper;

namespace RouteManager.Core.Net;

public interface IRouteTable
{
    IReadOnlyList<SystemRoute> GetRoutes();

    /// <summary>Adds a static route. Returns false if an identical route already exists.</summary>
    bool Add(RouteKey key, uint metric);

    /// <summary>Deletes a route. Returns false if it was not present.</summary>
    bool Delete(RouteKey key);
}

/// <summary>The live Windows routing table (IPv4 and IPv6). Add/Delete require administrator rights.</summary>
public sealed unsafe class WindowsRouteTable : IRouteTable
{
    public IReadOnlyList<SystemRoute> GetRoutes()
    {
        Check(GetIpForwardTable2(AF_UNSPEC, out var table), "read the routing table");
        try
        {
            var count = *(uint*)table;
            var rows = (MIB_IPFORWARD_ROW2*)((byte*)table + ForwardTableRowsOffset);
            var result = new List<SystemRoute>((int)count);
            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                var destination = new IpPrefix(row.DestinationPrefix.ToIPAddress(), row.DestinationPrefixLength);
                var key = new RouteKey(row.InterfaceLuid, destination, row.NextHop.ToIPAddress());
                result.Add(new SystemRoute(key, row.InterfaceIndex, row.Metric, row.Protocol));
            }
            return result;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    public bool Add(RouteKey key, uint metric)
    {
        var row = ToRow(key);
        row.Metric = metric;
        row.Protocol = MIB_IPPROTO_NETMGMT;
        var error = CreateIpForwardEntry2(&row);
        if (error == ERROR_OBJECT_ALREADY_EXISTS)
            return false;
        Check(error, $"add route {key}");
        return true;
    }

    public bool Delete(RouteKey key)
    {
        var row = ToRow(key);
        var error = DeleteIpForwardEntry2(&row);
        if (error == ERROR_NOT_FOUND)
            return false;
        Check(error, $"delete route {key}");
        return true;
    }

    private static MIB_IPFORWARD_ROW2 ToRow(RouteKey key)
    {
        MIB_IPFORWARD_ROW2 row;
        InitializeIpForwardEntry(&row);
        row.InterfaceLuid = key.InterfaceLuid;
        row.DestinationPrefix = SOCKADDR_INET.From(key.Destination.Network);
        row.DestinationPrefixLength = (byte)key.Destination.Length;
        // The next hop must carry the destination's family even when it is the unspecified (on-link) address.
        var nextHop = key.NextHop.AddressFamily == key.Destination.Family
            ? key.NextHop
            : key.Destination.Family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
        row.NextHop = SOCKADDR_INET.From(nextHop);
        return row;
    }

    private static void Check(int error, string action)
    {
        if (error == NO_ERROR)
            return;
        var message = error == ERROR_ACCESS_DENIED
            ? $"Could not {action}: administrator rights are required."
            : $"Could not {action}: {new Win32Exception(error).Message} (error {error}).";
        throw new RouteTableException(message, error);
    }
}

public sealed class RouteTableException(string message, int errorCode) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
}

/// <summary>Raises <see cref="Changed"/> (on a thread-pool thread) whenever the system routing table changes.</summary>
public sealed class RouteChangeWatcher : IDisposable
{
    private readonly IpHelper.RouteChangeCallback _callback; // kept alive for the native side
    private IntPtr _handle;

    public RouteChangeWatcher()
    {
        _callback = (_, _, _) => Changed?.Invoke(this, EventArgs.Empty);
        var error = NotifyRouteChange2(AF_UNSPEC, _callback, IntPtr.Zero, false, out _handle);
        if (error != NO_ERROR)
            throw new Win32Exception(error, "Could not subscribe to routing table changes.");
    }

    public event EventHandler? Changed;

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
            return;
        CancelMibChangeNotify2(_handle);
        _handle = IntPtr.Zero;
    }
}
