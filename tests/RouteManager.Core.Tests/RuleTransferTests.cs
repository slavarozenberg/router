using RouteManager.Core.Model;
using RouteManager.Core.Storage;

namespace RouteManager.Core.Tests;

public sealed class RuleTransferTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rm-transfer-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static RuleGroup Group(params Rule[] rules) => new() { Name = "Work", Rules = [.. rules] };

    [Fact]
    public void Export_round_trips_with_fresh_ids()
    {
        var rule = new Rule { Name = "Office", Matches = ["10.90.0.0/22"], Ipv4Interface = Nic.Ref(Nic.Vpn()) };
        var group = Group(rule);
        RuleTransfer.Export(PathOf("export.json"), [group]);

        var import = RuleTransfer.ImportFile(PathOf("export.json"), [Nic.Vpn()]);

        var imported = Assert.Single(Assert.Single(import.Groups).Rules);
        Assert.Equal("Office", imported.Name);
        Assert.Equal(["10.90.0.0/22"], imported.Matches);
        Assert.Equal(Nic.Ref(Nic.Vpn()), imported.Ipv4Interface);
        Assert.NotEqual(rule.Id, imported.Id);
        Assert.NotEqual(group.Id, import.Groups[0].Id);
        Assert.Empty(import.Warnings);
    }

    [Fact]
    public void Interface_from_another_computer_is_rebound_by_name()
    {
        var foreign = new InterfaceRef(123, "openvpn");
        RuleTransfer.Export(PathOf("export.json"), [Group(new Rule { Matches = ["10.0.0.0/8"], Ipv4Interface = foreign })]);

        var import = RuleTransfer.ImportFile(PathOf("export.json"), [Nic.Vpn(), Nic.Lan()]);

        Assert.Equal(Nic.Ref(Nic.Vpn()), import.Groups[0].Rules[0].Ipv4Interface);
        Assert.Empty(import.Warnings);
    }

    [Fact]
    public void Unknown_interface_is_kept_and_reported()
    {
        var foreign = new InterfaceRef(123, "Corp Tunnel");
        RuleTransfer.Export(PathOf("export.json"), [Group(new Rule { Matches = ["10.0.0.0/8"], Ipv4Interface = foreign })]);

        var import = RuleTransfer.ImportFile(PathOf("export.json"), [Nic.Vpn()]);

        Assert.Equal(foreign, import.Groups[0].Rules[0].Ipv4Interface);
        Assert.Contains("Corp Tunnel", Assert.Single(import.Warnings));
    }

    [Fact]
    public void Own_rules_json_is_accepted_as_backup()
    {
        var store = new RuleStore(new AppPaths(_dir));
        store.Save(Nic.RuleSet(new Rule { Matches = ["10.1.0.0/16"], Ipv4Interface = Nic.Ref(Nic.Lan()) }));

        var import = RuleTransfer.ImportFile(PathOf("rules.json"), [Nic.Lan()]);

        Assert.Equal(["10.1.0.0/16"], Assert.Single(Assert.Single(import.Groups).Rules).Matches);
    }

    [Fact]
    public void Rouler_file_is_detected()
    {
        File.WriteAllText(PathOf("rules.json"), """
            { "version": 3, "rule_groups": [ { "id": 0, "name": "All Other Apps", "group_match_type": "AllOtherApps",
              "rules": [ { "id": 1, "name": "Office", "enabled": true, "action_type": { "DivertAction": {
                "target_interface_ipv4": { "InterfaceLuid": 14918723538255872 }, "target_interface_ipv6": null,
                "matches": [ { "RemoteIpv4Subnet": "10.90.0.0/22" } ] } } } ] } ] }
            """);

        var import = RuleTransfer.ImportFile(PathOf("rules.json"), [Nic.Vpn()]);

        Assert.Equal("Office", Assert.Single(Assert.Single(import.Groups).Rules).Name);
    }

    [Theory]
    [InlineData("""{ "something": 1 }""")]
    [InlineData("""[ 1, 2 ]""")]
    public void Unrelated_json_is_rejected(string json)
    {
        File.WriteAllText(PathOf("other.json"), json);

        Assert.Throws<FormatException>(() => RuleTransfer.ImportFile(PathOf("other.json"), []));
    }
}
