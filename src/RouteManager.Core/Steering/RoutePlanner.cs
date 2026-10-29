using System.Net;
using System.Net.Sockets;
using RouteManager.Core.Model;
using RouteManager.Core.Net;

namespace RouteManager.Core.Steering;

public enum RuleState
{
    /// <summary>Rule, its group, or steering as a whole is switched off.</summary>
    Off,
    /// <summary>Every destination has a route.</summary>
    Active,
    /// <summary>Some destinations have routes, others could not be routed (see messages).</summary>
    Partial,
    /// <summary>Nothing could be routed (see messages).</summary>
    Inactive,
}

public sealed record RuleStatus(RuleState State, IReadOnlyList<string> Messages, int RouteCount)
{
    public static readonly RuleStatus Off = new(RuleState.Off, [], 0);
}

public sealed record PlannedRoute(RouteKey Key, uint Metric, Guid RuleId);

public sealed record RoutePlan(IReadOnlyList<PlannedRoute> Routes, IReadOnlyDictionary<Guid, RuleStatus> RuleStatuses);

/// <summary>
/// Turns rules into the set of system routes that should exist, given the current interfaces.
/// Pure: no system calls, so it can be previewed and tested.
/// </summary>
public static class RoutePlanner
{
    public static RoutePlan Plan(RuleSet rules, IReadOnlyList<InterfaceInfo> interfaces)
    {
        var routes = new List<PlannedRoute>();
        var statuses = new Dictionary<Guid, RuleStatus>();
        // First rule (in display order) to claim a destination wins it.
        var claimed = new Dictionary<IpPrefix, (RouteKey Key, string RuleName)>();
        var byLuid = interfaces.DistinctBy(i => i.Luid).ToDictionary(i => i.Luid);

        foreach (var group in rules.Groups)
        {
            foreach (var rule in group.Rules)
            {
                if (!rules.SteeringEnabled || !group.Enabled || !rule.Enabled)
                {
                    statuses[rule.Id] = RuleStatus.Off;
                    continue;
                }

                var messages = new List<string>();
                var wanted = 0;
                var planned = 0;
                var ruleName = DisplayName(rule);

                var prefixes = new List<IpPrefix>();
                foreach (var text in rule.Matches)
                {
                    if (AddressMatch.TryParse(text, out var match, out var error))
                        prefixes.AddRange(match.ToPrefixes());
                    else
                        messages.Add(error!);
                }

                foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
                {
                    var familyPrefixes = prefixes.Where(p => p.Family == family).Distinct().ToList();
                    if (familyPrefixes.Count == 0)
                        continue;
                    wanted += familyPrefixes.Count;

                    var label = family == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                    var target = family == AddressFamily.InterNetwork ? rule.Ipv4Interface : rule.Ipv6Interface;
                    if (target is null)
                    {
                        messages.Add($"No {label} interface selected for {familyPrefixes.Count} {label} destination(s).");
                        continue;
                    }
                    if (!byLuid.TryGetValue(target.Luid, out var nic))
                    {
                        messages.Add($"Interface \"{target.Name}\" is not present.");
                        continue;
                    }
                    if (!nic.CanCarry(family))
                    {
                        messages.Add(nic.IsUp
                            ? $"\"{nic.Name}\" has no usable {label} address."
                            : $"\"{nic.Name}\" is down.");
                        continue;
                    }

                    var nextHop = nic.GatewayFor(family)
                        ?? (family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any);
                    foreach (var prefix in familyPrefixes)
                    {
                        var key = new RouteKey(nic.Luid, prefix, nextHop);
                        if (claimed.TryGetValue(prefix, out var owner))
                        {
                            if (owner.Key.Equals(key))
                                planned++;
                            else
                                messages.Add($"{prefix} is already steered by \"{owner.RuleName}\".");
                            continue;
                        }

                        claimed[prefix] = (key, ruleName);
                        routes.Add(new PlannedRoute(key, rules.RouteMetric, rule.Id));
                        planned++;
                    }
                }

                var state = wanted == 0 ? RuleState.Inactive
                    : planned == wanted && messages.Count == 0 ? RuleState.Active
                    : planned > 0 ? RuleState.Partial
                    : RuleState.Inactive;
                if (wanted == 0 && messages.Count == 0)
                    messages.Add("No destinations.");
                statuses[rule.Id] = new RuleStatus(state, messages, planned);
            }
        }

        return new RoutePlan(routes, statuses);
    }

    public static string DisplayName(Rule rule) =>
        !string.IsNullOrWhiteSpace(rule.Name) ? rule.Name!
        : rule.Matches.Count > 0 ? string.Join(", ", rule.Matches.Take(2)) + (rule.Matches.Count > 2 ? ", …" : "")
        : "Unnamed rule";
}
