using System.Text.Json;
using RouteManager.Core.Model;

namespace RouteManager.Core.Storage;

/// <summary>
/// Imports Rouler's rules.json (format version 3) and interface names from its interfaces.json.
/// Rouler can scope a group to one app via its WFP driver; routes can't, so app-scoped groups
/// are imported switched off and flagged, because enabling them would steer every app.
/// </summary>
public static class RoulerImporter
{
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rouler");

    public static RuleImport ImportFile(string rulesPath)
    {
        var interfacesPath = Path.Combine(Path.GetDirectoryName(rulesPath)!, "interfaces.json");
        var interfacesJson = File.Exists(interfacesPath) ? File.ReadAllText(interfacesPath) : null;
        return Import(File.ReadAllText(rulesPath), interfacesJson);
    }

    public static RuleImport Import(string rulesJson, string? interfacesJson)
    {
        var names = ReadInterfaceNames(interfacesJson);
        var warnings = new List<string>();
        var groups = new List<RuleGroup>();

        using var doc = JsonDocument.Parse(rulesJson);
        if (!doc.RootElement.TryGetProperty("rule_groups", out var ruleGroups))
            throw new FormatException("Not a Rouler rules file: \"rule_groups\" is missing.");

        foreach (var sourceGroup in ruleGroups.EnumerateArray())
        {
            var groupName = sourceGroup.TryGetProperty("name", out var n) ? n.GetString() ?? "Rouler group" : "Rouler group";
            var group = new RuleGroup { Name = groupName };

            var matchType = sourceGroup.GetProperty("group_match_type");
            if (matchType.ValueKind == JsonValueKind.Object && matchType.TryGetProperty("AppPath", out var appPath))
            {
                group.Name = $"{groupName} (app-specific in Rouler)";
                group.Enabled = false;
                warnings.Add($"Group \"{groupName}\" applied only to {appPath.GetString()} in Rouler. " +
                              "It was imported switched off: as routes it would apply to all apps.");
            }

            foreach (var sourceRule in sourceGroup.GetProperty("rules").EnumerateArray())
            {
                var rule = ImportRule(sourceRule, names, groupName, warnings);
                if (rule is not null)
                    group.Rules.Add(rule);
            }

            groups.Add(group);
        }

        return new RuleImport(groups, warnings);
    }

    private static Rule? ImportRule(JsonElement source, IReadOnlyDictionary<ulong, string> names, string groupName, List<string> warnings)
    {
        var action = source.GetProperty("action_type");
        if (action.ValueKind != JsonValueKind.Object || !action.TryGetProperty("DivertAction", out var divert))
        {
            warnings.Add($"Skipped a rule in \"{groupName}\": only \"divert to interface\" rules can be imported.");
            return null;
        }

        var rule = new Rule
        {
            Name = source.TryGetProperty("name", out var name) ? name.GetString() : null,
            Enabled = !source.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
            Ipv4Interface = ReadInterface(divert, "target_interface_ipv4", names),
            Ipv6Interface = ReadInterface(divert, "target_interface_ipv6", names),
        };

        foreach (var match in divert.GetProperty("matches").EnumerateArray())
        {
            foreach (var property in match.EnumerateObject())
            {
                var text = property.Name switch
                {
                    "RemoteIpv4Address" or "RemoteIpv6Address" or "RemoteIpv4Subnet" or "RemoteIpv6Subnet"
                        => property.Value.GetString(),
                    "RemoteIpv4Range" or "RemoteIpv6Range"
                        => $"{property.Value.GetProperty("start").GetString()}-{property.Value.GetProperty("end").GetString()}",
                    _ => null,
                };
                if (text is null)
                    warnings.Add($"Skipped unsupported match \"{property.Name}\" in \"{groupName}\".");
                else
                    rule.Matches.Add(text);
            }
        }

        return rule;
    }

    private static InterfaceRef? ReadInterface(JsonElement divert, string property, IReadOnlyDictionary<ulong, string> names)
    {
        if (!divert.TryGetProperty(property, out var target) || target.ValueKind != JsonValueKind.Object
            || !target.TryGetProperty("InterfaceLuid", out var luidElement))
            return null;
        var luid = luidElement.GetUInt64();
        return new InterfaceRef(luid, names.GetValueOrDefault(luid) ?? $"Interface {luid}");
    }

    private static Dictionary<ulong, string> ReadInterfaceNames(string? interfacesJson)
    {
        var names = new Dictionary<ulong, string>();
        if (interfacesJson is null)
            return names;

        using var doc = JsonDocument.Parse(interfacesJson);
        if (!doc.RootElement.TryGetProperty("interfaces", out var interfaces))
            return names;
        foreach (var entry in interfaces.EnumerateObject())
        {
            if (!ulong.TryParse(entry.Name, out var luid))
                continue;
            var custom = entry.Value.TryGetProperty("custom_name", out var c) ? c.GetString() : null;
            var observed = entry.Value.TryGetProperty("observed_name", out var o) ? o.GetString() : null;
            if ((custom ?? observed) is { } display)
                names[luid] = display;
        }
        return names;
    }
}
