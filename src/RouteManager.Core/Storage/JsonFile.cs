using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RouteManager.Core.Model;
using RouteManager.Core.Net;

namespace RouteManager.Core.Storage;

/// <summary>Reads and atomically writes JSON documents.</summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keeps the file hand-editable: "Lab (v4+v6)" instead of "Lab (v4+v6)". Local file, never served as HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T? Read<T>(string path)
    {
        if (!File.Exists(path))
            return default;
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options);
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Where the app keeps its state. Routes are machine-wide, so the state is too.</summary>
public sealed class AppPaths(string root)
{
    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RouteManager"));

    public string Root { get; } = root;
    public string Rules => Path.Combine(Root, "rules.json");
    public string Ledger => Path.Combine(Root, "applied-routes.json");
}

public sealed class RuleStore(AppPaths paths)
{
    public RuleSet Load() => JsonFile.Read<RuleSet>(paths.Rules) ?? CreateDefault();

    public void Save(RuleSet rules) => JsonFile.Write(paths.Rules, rules);

    private static RuleSet CreateDefault() => new() { Groups = [new RuleGroup { Name = "Default" }] };
}

/// <summary>The routes this app created and is therefore allowed to delete.</summary>
public sealed class LedgerStore(AppPaths paths)
{
    private sealed record Entry(ulong InterfaceLuid, string Destination, string NextHop);

    public IReadOnlyList<RouteKey> Load()
    {
        var entries = JsonFile.Read<List<Entry>>(paths.Ledger) ?? [];
        return entries
            .Select(e => new RouteKey(e.InterfaceLuid, IpPrefix.Parse(e.Destination), IPAddress.Parse(e.NextHop)))
            .ToList();
    }

    public void Save(IEnumerable<RouteKey> keys) =>
        JsonFile.Write(paths.Ledger, keys
            .Select(k => new Entry(k.InterfaceLuid, k.Destination.ToString(), k.NextHop.ToString()))
            .ToList());
}
