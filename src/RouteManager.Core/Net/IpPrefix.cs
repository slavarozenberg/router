using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace RouteManager.Core.Net;

/// <summary>A normalized network prefix (host bits cleared), e.g. 10.90.0.0/22 or fd00::/8.</summary>
public readonly record struct IpPrefix
{
    public IpPrefix(IPAddress address, int length)
    {
        var width = IpMath.BitWidth(address.AddressFamily);
        if (length < 0 || length > width)
            throw new ArgumentOutOfRangeException(nameof(length), $"Prefix length must be 0..{width}.");

        var network = IpMath.ToInteger(address) & IpMath.Mask(length, width);
        Network = IpMath.FromInteger(network, address.AddressFamily);
        Length = length;
    }

    public IPAddress Network { get; }
    public int Length { get; }
    public AddressFamily Family => Network.AddressFamily;
    public bool IsIPv4 => Family == AddressFamily.InterNetwork;

    public static IpPrefix Host(IPAddress address) => new(address, IpMath.BitWidth(address.AddressFamily));

    /// <summary>Parses "addr/len" or "addr/netmask" (IPv4). Host bits are cleared.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out IpPrefix? prefix)
    {
        prefix = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !IsSupported(address))
            return false;

        int length;
        if (int.TryParse(parts[1], out var parsed))
            length = parsed;
        else if (address.AddressFamily == AddressFamily.InterNetwork
                 && IPAddress.TryParse(parts[1], out var mask)
                 && IpMath.PrefixLengthFromMask(mask) is { } fromMask)
            length = fromMask;
        else
            return false;

        if (length < 0 || length > IpMath.BitWidth(address.AddressFamily))
            return false;

        prefix = new IpPrefix(address, length);
        return true;
    }

    public static IpPrefix Parse(string text) =>
        TryParse(text, out var prefix) ? prefix.Value : throw new FormatException($"'{text}' is not a valid prefix.");

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != Family)
            return false;
        var width = IpMath.BitWidth(Family);
        return (IpMath.ToInteger(address) & IpMath.Mask(Length, width)) == IpMath.ToInteger(Network);
    }

    internal static bool IsSupported(IPAddress address) =>
        address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;

    public override string ToString() => $"{Network}/{Length}";
}
