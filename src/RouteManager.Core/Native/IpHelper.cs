using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RouteManager.Core.Native;

/// <summary>P/Invoke surface for the IP Helper routing APIs (iphlpapi.dll, Windows Vista+).</summary>
internal static unsafe class IpHelper
{
    public const ushort AF_UNSPEC = 0;
    public const ushort AF_INET = 2;
    public const ushort AF_INET6 = 23;

    public const int NO_ERROR = 0;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NOT_FOUND = 1168;
    public const int ERROR_OBJECT_ALREADY_EXISTS = 5010;

    /// <summary>MIB_IPPROTO_NETMGMT: a static route, the same protocol `route add` uses.</summary>
    public const int MIB_IPPROTO_NETMGMT = 3;

    /// <summary>SOCKADDR_INET: union of sockaddr_in / sockaddr_in6, 28 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 28)]
    public struct SOCKADDR_INET
    {
        public ushort Family;
        // sockaddr_in : port[2] addr[4] zero[8]
        // sockaddr_in6: port[2] flowinfo[4] addr[16] scope_id[4]
        public fixed byte Data[26];

        public static SOCKADDR_INET From(IPAddress address)
        {
            var result = default(SOCKADDR_INET);
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                result.Family = AF_INET;
                for (var i = 0; i < 4; i++)
                    result.Data[2 + i] = bytes[i];
            }
            else
            {
                result.Family = AF_INET6;
                for (var i = 0; i < 16; i++)
                    result.Data[6 + i] = bytes[i];
                var scope = (uint)address.ScopeId;
                for (var i = 0; i < 4; i++)
                    result.Data[22 + i] = (byte)(scope >> (8 * i));
            }
            return result;
        }

        public readonly IPAddress ToIPAddress()
        {
            if (Family == AF_INET)
            {
                var v4 = new byte[4];
                for (var i = 0; i < 4; i++)
                    v4[i] = Data[2 + i];
                return new IPAddress(v4);
            }

            var v6 = new byte[16];
            for (var i = 0; i < 16; i++)
                v6[i] = Data[6 + i];
            return new IPAddress(v6);
        }
    }

    /// <summary>MIB_IPFORWARD_ROW2 (netioapi.h), 104 bytes on x86 and x64.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    public struct MIB_IPFORWARD_ROW2
    {
        [FieldOffset(0)] public ulong InterfaceLuid;
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(12)] public SOCKADDR_INET DestinationPrefix;
        [FieldOffset(40)] public byte DestinationPrefixLength;
        [FieldOffset(44)] public SOCKADDR_INET NextHop;
        [FieldOffset(72)] public byte SitePrefixLength;
        [FieldOffset(76)] public uint ValidLifetime;
        [FieldOffset(80)] public uint PreferredLifetime;
        [FieldOffset(84)] public uint Metric;
        [FieldOffset(88)] public int Protocol;
        [FieldOffset(92)] public byte Loopback;
        [FieldOffset(93)] public byte AutoconfigureAddress;
        [FieldOffset(94)] public byte Publish;
        [FieldOffset(95)] public byte Immortal;
        [FieldOffset(96)] public uint Age;
        [FieldOffset(100)] public int Origin;
    }

    /// <summary>Offset of MIB_IPFORWARD_TABLE2.Table: ULONG NumEntries padded to the row's 8-byte alignment.</summary>
    public const int ForwardTableRowsOffset = 8;

    public delegate void RouteChangeCallback(IntPtr callerContext, IntPtr row, int notificationType);

    [DllImport("iphlpapi.dll")]
    public static extern int GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    public static extern void FreeMibTable(IntPtr memory);

    [DllImport("iphlpapi.dll")]
    public static extern void InitializeIpForwardEntry(MIB_IPFORWARD_ROW2* row);

    [DllImport("iphlpapi.dll")]
    public static extern int CreateIpForwardEntry2(MIB_IPFORWARD_ROW2* row);

    [DllImport("iphlpapi.dll")]
    public static extern int DeleteIpForwardEntry2(MIB_IPFORWARD_ROW2* row);

    [DllImport("iphlpapi.dll")]
    public static extern int NotifyRouteChange2(
        ushort family, RouteChangeCallback callback, IntPtr callerContext,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification, out IntPtr notificationHandle);

    [DllImport("iphlpapi.dll")]
    public static extern int CancelMibChangeNotify2(IntPtr notificationHandle);

    [DllImport("iphlpapi.dll")]
    public static extern int ConvertInterfaceGuidToLuid(in Guid interfaceGuid, out ulong interfaceLuid);

    [DllImport("iphlpapi.dll")]
    public static extern int GetBestRoute2(
        ulong* interfaceLuid, uint interfaceIndex, SOCKADDR_INET* sourceAddress, SOCKADDR_INET* destinationAddress,
        uint addressSortOptions, MIB_IPFORWARD_ROW2* bestRoute, SOCKADDR_INET* bestSourceAddress);

    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int TCP_TABLE_OWNER_PID_ALL = 5;

    /// <summary>MIB_TCPROW_OWNER_PID (tcpmib.h). Ports hold a network-order ushort in their low 16 bits.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>MIB_TCP6ROW_OWNER_PID (tcpmib.h), 56 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCP6ROW_OWNER_PID
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    /// <summary>Offset of MIB_TCPTABLE_OWNER_PID.table / MIB_TCP6TABLE_OWNER_PID.table: DWORD dwNumEntries.</summary>
    public const int TcpTableRowsOffset = 4;

    [DllImport("iphlpapi.dll")]
    public static extern int GetExtendedTcpTable(
        void* tcpTable, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
