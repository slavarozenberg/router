using System.Net;
using System.Net.Sockets;

namespace RouteManager.Core.Net;

/// <summary>Converts IPv4/IPv6 addresses to and from 128-bit integers for prefix arithmetic.</summary>
public static class IpMath
{
    public static int BitWidth(AddressFamily family) => family switch
    {
        AddressFamily.InterNetwork => 32,
        AddressFamily.InterNetworkV6 => 128,
        _ => throw new ArgumentException($"Unsupported address family {family}.", nameof(family)),
    };

    public static UInt128 ToInteger(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written))
            throw new ArgumentException("Address could not be serialized.", nameof(address));

        UInt128 value = 0;
        for (var i = 0; i < written; i++)
            value = (value << 8) | bytes[i];
        return value;
    }

    public static IPAddress FromInteger(UInt128 value, AddressFamily family)
    {
        var length = BitWidth(family) / 8;
        var bytes = new byte[length];
        for (var i = length - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }
        return new IPAddress(bytes);
    }

    /// <summary>Mask with the top <paramref name="prefixLength"/> bits of a <paramref name="width"/>-bit address set.</summary>
    public static UInt128 Mask(int prefixLength, int width)
    {
        if (prefixLength == 0)
            return 0;
        var all = width == 128 ? UInt128.MaxValue : (UInt128.One << width) - 1;
        return all ^ ((UInt128.One << (width - prefixLength)) - 1);
    }

    /// <summary>Prefix length of a contiguous IPv4 netmask such as 255.255.252.0, or null if not contiguous.</summary>
    public static int? PrefixLengthFromMask(IPAddress mask)
    {
        if (mask.AddressFamily != AddressFamily.InterNetwork)
            return null;
        var value = ToInteger(mask);
        for (var length = 0; length <= 32; length++)
            if (Mask(length, 32) == value)
                return length;
        return null;
    }
}
