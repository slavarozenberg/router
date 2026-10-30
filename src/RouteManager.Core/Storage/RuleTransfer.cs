using System.Text.Json;
using RouteManager.Core.Model;
using RouteManager.Core.Net;

namespace RouteManager.Core.Storage;

public sealed record RuleImport(IReadOnlyList<RuleGroup> Groups, IReadOnlyList<string> Warnings);

/// <summary>The exported file: rule groups only, without machine settings such as the master switch.</summary>
public sealed class RuleExport
{
    public const string FormatName = "route-manager-rules";

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    public DateTime? ExportedAt { get; set; }
    public List<RuleGroup> Groups { get; set; } = [];
}

/// <summary>
/// Export and import of rules. Import accepts an export, the app's own rules.json (a backup), or Rouler's
/// rules.json. Imported groups and rules get fresh IDs, and interfaces are re-bound by name when the file
/// comes from another computer, where the same adapter has a different LUID.
/// </summary>
public static class RuleTransfer
{
    public static void Export(string path, IEnumerable<RuleGroup> groups) =>
        JsonFile.Write(path, new RuleExport { ExportedAt = DateTime.Now, Groups = [.. groups] });

    public static RuleImport ImportFile(string path, IReadOnlyList<InterfaceInfo> interfaces)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("Not a rules file: expected a JSON object.");

        if (doc.RootElement.TryGetProperty("rule_groups", out _))
            return Rebind(RoulerImporter.ImportFile(path), interfaces);

        if (!doc.RootElement.TryGetProperty("groups", out _))
            throw new FormatException("Not a Route Manager or Rouler rules file: \"groups\" is missing.");
        // An export and rules.json share the "groups" shape; extra rules.json settings are ignored.
        var export = JsonSerializer.Deserialize<RuleExport>(json, JsonFile.Options)!;
        return Rebind(new RuleImport(export.Groups ?? [], []), interfaces);
    }

    /// <summary>Gives every group and rule a new ID and points interface references at this computer's adapters.</summary>
    public static RuleImport Rebind(RuleImport import, IReadOnlyList<InterfaceInfo> interfaces)
    {
        var warnings = new List<string>(import.Warnings);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in import.Groups)
        {
            group.Id = Guid.NewGuid();
            group.Name ??= "Imported";
            group.Rules ??= [];
            foreach (var rule in group.Rules)
            {
                rule.Id = Guid.NewGuid();
                rule.Matches ??= [];
                rule.Ipv4Interface = Resolve(rule.Ipv4Interface, interfaces, missing);
                rule.Ipv6Interface = Resolve(rule.Ipv6Interface, interfaces, missing);
            }
        }

        if (missing.Count > 0)
            warnings.Add($"Not found on this computer: {string.Join(", ", missing)}. " +
                         "Rules that use these interfaces stay inactive until you choose another interface for them.");
        return new RuleImport(import.Groups, warnings);
    }

    private static InterfaceRef? Resolve(InterfaceRef? reference, IReadOnlyList<InterfaceInfo> interfaces, ISet<string> missing)
    {
        if (reference is null || interfaces.Any(i => i.Luid == reference.Luid))
            return reference;
        var byName = interfaces.Where(i => string.Equals(i.Name, reference.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1)
            return new InterfaceRef(byName[0].Luid, byName[0].Name);
        missing.Add(reference.Name);
        return reference;
    }
}
