using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using static RouteManager.Core.Native.IpHelper;

namespace RouteManager.Core.Net;

/// <summary>A TCP connection and the process that owns it. IPv4-mapped IPv6 addresses are reported as IPv4.</summary>
public sealed record TcpConnection(int ProcessId, IPEndPoint Local, IPEndPoint Remote, TcpState State);

public interface IConnectionTable
{
    IReadOnlyList<TcpConnection> GetTcpConnections();

    /// <summary>The route Windows uses to send from <paramref name="source"/> to <paramref name="destination"/>, or null if none.</summary>
    RouteKey? GetBestRoute(IPAddress source, IPAddress destination);
}

/// <summary>Live TCP connections (IPv4 and IPv6) with owning process IDs. Works without administrator rights.</summary>
public sealed unsafe class WindowsConnectionTable : IConnectionTable
{
    public IReadOnlyList<TcpConnection> GetTcpConnections()
    {
        var result = new List<TcpConnection>();
        ReadTable(AF_INET, table =>
        {
            var count = *(uint*)table;
            var rows = (MIB_TCPROW_OWNER_PID*)((byte*)table + TcpTableRowsOffset);
            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                result.Add(new TcpConnection(
                    (int)row.OwningPid,
                    new IPEndPoint(new IPAddress(row.LocalAddr), Port(row.LocalPort)),
                    new IPEndPoint(new IPAddress(row.RemoteAddr), Port(row.RemotePort)),
                    (TcpState)row.State));
            }
        });
        ReadTable(AF_INET6, table =>
        {
            var count = *(uint*)table;
            var rows = (MIB_TCP6ROW_OWNER_PID*)((byte*)table + TcpTableRowsOffset);
            for (var i = 0; i < count; i++)
            {
                var row = &rows[i];
                result.Add(new TcpConnection(
                    (int)row->OwningPid,
                    new IPEndPoint(Unmap(new IPAddress(new ReadOnlySpan<byte>(row->LocalAddr, 16), row->LocalScopeId)), Port(row->LocalPort)),
                    new IPEndPoint(Unmap(new IPAddress(new ReadOnlySpan<byte>(row->RemoteAddr, 16), row->RemoteScopeId)), Port(row->RemotePort)),
                    (TcpState)row->State));
            }
        });
        return result;
    }

    public RouteKey? GetBestRoute(IPAddress source, IPAddress destination)
    {
        var target = SOCKADDR_INET.From(destination);
        var from = SOCKADDR_INET.From(source);
        // A bound source pins the lookup to that address's interface, which is how Windows routes an established connection.
        var useSource = source.AddressFamily == destination.AddressFamily
                        && !source.Equals(IPAddress.Any) && !source.Equals(IPAddress.IPv6Any);
        MIB_IPFORWARD_ROW2 row;
        SOCKADDR_INET bestSource;
        if (GetBestRoute2(null, 0, useSource ? &from : null, &target, 0, &row, &bestSource) != NO_ERROR)
            return null;
        var prefix = new IpPrefix(row.DestinationPrefix.ToIPAddress(), row.DestinationPrefixLength);
        return new RouteKey(row.InterfaceLuid, prefix, row.NextHop.ToIPAddress());
    }

    private delegate void TableReader(IntPtr table);

    private static void ReadTable(ushort family, TableReader read)
    {
        var size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(size);
            try
            {
                var error = GetExtendedTcpTable((void*)buffer, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
                if (error == ERROR_INSUFFICIENT_BUFFER)
                    continue; // size now holds what is needed; connections may appear between calls, so retry
                if (error != NO_ERROR)
                    throw new Win32Exception(error, "Could not read the TCP connection table.");
                read(buffer);
                return;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }
        throw new Win32Exception(ERROR_INSUFFICIENT_BUFFER, "The TCP connection table kept growing while being read.");
    }

    private static int Port(uint networkOrder) => (int)(((networkOrder & 0xFF) << 8) | ((networkOrder >> 8) & 0xFF));

    private static IPAddress Unmap(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
