using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace RouteManager.Core.Net;

public enum AddressMatchKind
{
    Address,
    Range,
    Subnet,
}

/// <summary>
/// A destination a rule applies to: a single address, an inclusive range, or a subnet.
/// Text forms: "10.0.0.5", "10.80.90.0-10.80.90.255", "10.90.0.0/22" (IPv6 equivalents too).
/// </summary>
public sealed record AddressMatch
{
    private AddressMatch(AddressMatchKind kind, IPAddress start, IPAddress end, IpPrefix? subnet)
    {
        Kind = kind;
        Start = start;
        End = end;
        Subnet = subnet;
    }

    public AddressMatchKind Kind { get; }
    public IPAddress Start { get; }
    public IPAddress End { get; }
    public IpPrefix? Subnet { get; }
    public AddressFamily Family => Start.AddressFamily;

    public static AddressMatch ForAddress(IPAddress address) =>
        new(AddressMatchKind.Address, address, address, null);

    public static AddressMatch ForSubnet(IpPrefix subnet) =>
        new(AddressMatchKind.Subnet, subnet.Network, subnet.Network, subnet);

    public static AddressMatch ForRange(IPAddress start, IPAddress end)
    {
        if (start.AddressFamily != end.AddressFamily)
            throw new ArgumentException("Range start and end must be the same address family.");
        if (IpMath.ToInteger(start) > IpMath.ToInteger(end))
            throw new ArgumentException("Range start must not be greater than range end.");
        return new(AddressMatchKind.Range, start, end, null);
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out AddressMatch? match, out string? error)
    {
        match = null;
        error = null;
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            error = "Empty entry.";
            return false;
        }

        if (trimmed.Contains('/'))
        {
            if (IpPrefix.TryParse(trimmed, out var prefix))
            {
                match = ForSubnet(prefix.Value);
                return true;
            }
            error = $"'{trimmed}' is not a valid subnet.";
            return false;
        }

        var dash = trimmed.IndexOf('-');
        if (dash > 0)
        {
            if (IPAddress.TryParse(trimmed[..dash].Trim(), out var start)
                && IPAddress.TryParse(trimmed[(dash + 1)..].Trim(), out var end)
                && IpPrefix.IsSupported(start)
                && start.AddressFamily == end.AddressFamily)
            {
                if (IpMath.ToInteger(start) > IpMath.ToInteger(end))
                {
                    error = $"'{trimmed}': range start is after range end.";
                    return false;
                }
                match = ForRange(start, end);
                return true;
            }
            error = $"'{trimmed}' is not a valid address range.";
            return false;
        }

        if (IPAddress.TryParse(trimmed, out var address) && IpPrefix.IsSupported(address))
        {
            match = ForAddress(address);
            return true;
        }

        error = $"'{trimmed}' is not a valid address, range or subnet.";
        return false;
    }

    public static AddressMatch Parse(string text) =>
        TryParse(text, out var match, out var error) ? match : throw new FormatException(error);

    /// <summary>The minimal set of prefixes that exactly covers this match.</summary>
    public IReadOnlyList<IpPrefix> ToPrefixes() => Kind switch
    {
        AddressMatchKind.Address => [IpPrefix.Host(Start)],
        AddressMatchKind.Subnet => [Subnet!.Value],
        _ => RangeToPrefixes(Start, End),
    };

    /// <summary>Splits an inclusive address range into the minimal list of aligned CIDR blocks.</summary>
    public static IReadOnlyList<IpPrefix> RangeToPrefixes(IPAddress start, IPAddress end)
    {
        var family = start.AddressFamily;
        var width = IpMath.BitWidth(family);
        var current = IpMath.ToInteger(start);
        var last = IpMath.ToInteger(end);
        var result = new List<IpPrefix>();

        while (true)
        {
            // Largest block aligned at `current` that does not run past `last`.
            var hostBits = current == 0 ? width : (int)UInt128.TrailingZeroCount(current);
            while (hostBits > 0 && BlockEnd(current, hostBits) > last)
                hostBits--;

            result.Add(new IpPrefix(IpMath.FromInteger(current, family), width - hostBits));

            var blockEnd = BlockEnd(current, hostBits);
            if (blockEnd == last)
                return result;
            current = blockEnd + 1;
        }
    }

    // `start` is aligned to 2^hostBits, so this cannot overflow.
    private static UInt128 BlockEnd(UInt128 start, int hostBits) =>
        hostBits == 128 ? UInt128.MaxValue : start + ((UInt128.One << hostBits) - 1);

    public override string ToString() => Kind switch
    {
        AddressMatchKind.Address => Start.ToString(),
        AddressMatchKind.Subnet => Subnet!.Value.ToString(),
        _ => $"{Start}-{End}",
    };
}
