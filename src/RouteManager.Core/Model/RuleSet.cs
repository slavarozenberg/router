using System.Text.Json;

namespace RouteManager.Core.Model;

/// <summary>Everything the user configures; persisted as rules.json.</summary>
public sealed class RuleSet
{
    public int Version { get; set; } = 1;

    /// <summary>Master switch. When off, every route the app created is removed.</summary>
    public bool SteeringEnabled { get; set; }

    /// <summary>Route metric for created routes (added to the interface metric by Windows).</summary>
    public uint RouteMetric { get; set; } = 5;

    public List<RuleGroup> Groups { get; set; } = [];

    /// <summary>Deep copy, so a background apply never sees the UI mutating the rules.</summary>
    public RuleSet Clone() => JsonSerializer.Deserialize<RuleSet>(JsonSerializer.Serialize(this))!;
}

/// <summary>A named folder of rules that can be switched on and off together.</summary>
public sealed class RuleGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<Rule> Rules { get; set; } = [];
}

/// <summary>Sends traffic for the listed destinations through the chosen interface(s).</summary>
public sealed class Rule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Destinations in text form: "10.0.0.5", "10.80.90.0-10.80.90.255", "10.90.0.0/22", IPv6 alike.</summary>
    public List<string> Matches { get; set; } = [];

    public InterfaceRef? Ipv4Interface { get; set; }
    public InterfaceRef? Ipv6Interface { get; set; }

    public Rule Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Matches = [.. Matches],
        Ipv4Interface = Ipv4Interface,
        Ipv6Interface = Ipv6Interface,
    };
}

/// <summary>An interface by LUID (stable across reboots), plus the name it had when chosen, for display while absent.</summary>
public sealed record InterfaceRef(ulong Luid, string Name);
