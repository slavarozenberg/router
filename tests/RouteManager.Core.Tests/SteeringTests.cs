using System.Net;
using RouteManager.Core.Model;
using RouteManager.Core.Net;
using RouteManager.Core.Steering;
using RouteManager.Core.Storage;

namespace RouteManager.Core.Tests;

public class RoutePlannerTests
{
    [Fact]
    public void Tunnel_without_gateway_gets_on_link_routes()
    {
        var rule = new Rule { Matches = ["10.90.0.0/22", "10.80.90.0-10.80.90.255"], Ipv4Interface = Nic.Ref(Nic.Vpn()) };

        var plan = RoutePlanner.Plan(Nic.RuleSet(rule), [Nic.Vpn()]);

        Assert.Equal(RuleState.Active, plan.RuleStatuses[rule.Id].State);
        Assert.Equal(
            ["10.90.0.0/22 via on-link", "10.80.90.0/24 via on-link"],
            plan.Routes.Select(r => $"{r.Key.Destination} via {(r.Key.IsOnLink ? "on-link" : r.Key.NextHop)}"));
    }

    [Fact]
    public void Interface_with_gateway_routes_via_gateway()
    {
        var rule = new Rule { Matches = ["172.20.2.0/24"], Ipv4Interface = Nic.Ref(Nic.Lan()) };

        var route = RoutePlanner.Plan(Nic.RuleSet(rule), [Nic.Lan()]).Routes.Single();

        Assert.Equal(IPAddress.Parse("172.19.4.1"), route.Key.NextHop);
        Assert.Equal(Nic.LanLuid, route.Key.InterfaceLuid);
    }

    [Fact]
    public void Down_interface_makes_rule_inactive()
    {
        var rule = new Rule { Matches = ["172.20.2.0/24"], Ipv4Interface = Nic.Ref(Nic.Vpn()) };

        var plan = RoutePlanner.Plan(Nic.RuleSet(rule), [Nic.Vpn(up: false)]);

        Assert.Empty(plan.Routes);
        Assert.Equal(RuleState.Inactive, plan.RuleStatuses[rule.Id].State);
        Assert.Contains("is down", plan.RuleStatuses[rule.Id].Messages.Single());
    }

    [Fact]
    public void Missing_family_target_makes_rule_partial()
    {
        var rule = new Rule { Matches = ["10.1.0.0/16", "2001:db8:1::/48"], Ipv4Interface = Nic.Ref(Nic.Lan()) };

        var status = RoutePlanner.Plan(Nic.RuleSet(rule), [Nic.Lan()]).RuleStatuses[rule.Id];

        Assert.Equal(RuleState.Partial, status.State);
        Assert.Contains("No IPv6 interface selected", status.Messages.Single());
    }

    [Fact]
    public void Interface_without_ipv6_address_cannot_carry_ipv6()
    {
        var rule = new Rule { Matches = ["2001:db8:1::/48"], Ipv6Interface = Nic.Ref(Nic.Vpn()) };

        var status = RoutePlanner.Plan(Nic.RuleSet(rule), [Nic.Vpn()]).RuleStatuses[rule.Id];

        Assert.Equal(RuleState.Inactive, status.State);
        Assert.Contains("no usable IPv6 address", status.Messages.Single());
    }

    [Fact]
    public void First_rule_wins_a_contested_destination()
    {
        var first = new Rule { Name = "first", Matches = ["10.1.0.0/16"], Ipv4Interface = Nic.Ref(Nic.Vpn()) };
        var second = new Rule { Name = "second", Matches = ["10.1.0.0/16"], Ipv4Interface = Nic.Ref(Nic.Lan()) };

        var plan = RoutePlanner.Plan(Nic.RuleSet(first, second), [Nic.Vpn(), Nic.Lan()]);

        Assert.Equal(Nic.VpnLuid, plan.Routes.Single().Key.InterfaceLuid);
        Assert.Equal(RuleState.Inactive, plan.RuleStatuses[second.Id].State);
        Assert.Contains("\"first\"", plan.RuleStatuses[second.Id].Messages.Single());
    }

    [Fact]
    public void Disabled_rule_group_or_master_switch_plans_nothing()
    {
        var rule = new Rule { Matches = ["10.1.0.0/16"], Ipv4Interface = Nic.Ref(Nic.Vpn()) };
        var rules = Nic.RuleSet(rule);

        rules.SteeringEnabled = false;
        Assert.Empty(RoutePlanner.Plan(rules, [Nic.Vpn()]).Routes);

        rules.SteeringEnabled = true;
        rules.Groups[0].Enabled = false;
        Assert.Empty(RoutePlanner.Plan(rules, [Nic.Vpn()]).Routes);
        Assert.Equal(RuleState.Off, RoutePlanner.Plan(rules, [Nic.Vpn()]).RuleStatuses[rule.Id].State);
    }
}

public class ReconcilerTests
{
    private static RouteKey Key(string prefix) => new(1, IpPrefix.Parse(prefix), IPAddress.Any);

    [Fact]
    public void Adds_missing_deletes_stale_owned_and_ignores_foreign()
    {
        var planned = new[] { new PlannedRoute(Key("10.1.0.0/16"), 5, Guid.Empty), new PlannedRoute(Key("10.2.0.0/16"), 5, Guid.Empty) };
        var ledger = new[] { Key("10.2.0.0/16"), Key("10.3.0.0/16") };
        var live = new[] { Key("10.2.0.0/16"), Key("10.3.0.0/16"), Key("192.168.0.0/16") };

        var actions = Reconciler.Diff(planned, ledger, live);

        Assert.Equal([Key("10.1.0.0/16")], actions.ToAdd.Select(a => a.Key));
        Assert.Equal([Key("10.3.0.0/16")], actions.ToDelete);
        Assert.Equal([Key("10.2.0.0/16")], actions.Keep);
    }

    [Fact]
    public void Owned_route_that_vanished_is_re_added()
    {
        var planned = new[] { new PlannedRoute(Key("10.1.0.0/16"), 5, Guid.Empty) };

        var actions = Reconciler.Diff(planned, [Key("10.1.0.0/16")], []);

        Assert.Single(actions.ToAdd);
        Assert.Empty(actions.Keep);
    }
}

public sealed class SteeringEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RouteManagerTests", Guid.NewGuid().ToString("N"));
    private readonly FakeRouteTable _table = new();
    private readonly FakeCatalog _catalog = new(Nic.Vpn(), Nic.Lan());
    private readonly SteeringEngine _engine;

    public SteeringEngineTests() =>
        _engine = new SteeringEngine(_table, _catalog, new LedgerStore(new AppPaths(_dir)));

    [Fact]
    public void Apply_creates_routes_and_switching_off_removes_only_owned_routes()
    {
        var foreign = new RouteKey(Nic.LanLuid, IpPrefix.Parse("192.168.0.0/16"), IPAddress.Parse("172.19.4.1"));
        _table.Routes.Add(foreign);
        var rules = Nic.RuleSet(new Rule { Matches = ["10.90.0.0/22", "172.20.3.0/24"], Ipv4Interface = Nic.Ref(Nic.Vpn()) });

        var result = _engine.Apply(rules, canWrite: true, "test");
        Assert.Equal(2, result.Added);
        Assert.Equal(3, _table.Routes.Count);

        rules.SteeringEnabled = false;
        result = _engine.Apply(rules, canWrite: true, "test");
        Assert.Equal(2, result.Removed);
        Assert.Equal([foreign], _table.Routes);
    }

    [Fact]
    public void Moving_rule_to_another_interface_replaces_its_routes()
    {
        var rule = new Rule { Matches = ["172.20.3.0/24"], Ipv4Interface = Nic.Ref(Nic.Vpn()) };
        var rules = Nic.RuleSet(rule);
        _engine.Apply(rules, canWrite: true, "test");

        rule.Ipv4Interface = Nic.Ref(Nic.Lan());
        _engine.Apply(rules, canWrite: true, "test");

        var route = Assert.Single(_table.Routes);
        Assert.Equal(Nic.LanLuid, route.InterfaceLuid);
    }

    [Fact]
    public void Read_only_apply_changes_nothing_but_reports_plan()
    {
        var rule = new Rule { Matches = ["172.20.3.0/24"], Ipv4Interface = Nic.Ref(Nic.Vpn()) };

        var result = _engine.Apply(Nic.RuleSet(rule), canWrite: false, "test");

        Assert.Empty(_table.Routes);
        Assert.Single(result.Plan.Routes);
    }

    [Fact]
    public void Ledger_survives_restart()
    {
        var rules = Nic.RuleSet(new Rule { Matches = ["172.20.3.0/24"], Ipv4Interface = Nic.Ref(Nic.Vpn()) });
        _engine.Apply(rules, canWrite: true, "test");

        using var restarted = new SteeringEngine(_table, _catalog, new LedgerStore(new AppPaths(_dir)));
        rules.SteeringEnabled = false;
        restarted.Apply(rules, canWrite: true, "test");

        Assert.Empty(_table.Routes);
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}

public class RoulerImporterTests
{
    private const string RulesJson = """
        {
          "version": 3,
          "rule_groups": [
            {
              "id": 1, "name": "TightVNC", "is_deletable": true,
              "group_match_type": { "AppPath": "C:\\Program Files\\TightVNC\\tvnviewer.exe" },
              "rules": [ { "id": 4, "name": null, "enabled": true, "is_deletable": true,
                "action_type": { "DivertAction": {
                  "target_interface_ipv4": { "InterfaceLuid": 14918723555033088 }, "target_interface_ipv6": null,
                  "matches": [ { "RemoteIpv4Address": "172.19.4.185" } ] } } } ]
            },
            {
              "id": 0, "name": "All Other Apps", "is_deletable": false, "group_match_type": "AllOtherApps",
              "rules": [ { "id": 1, "name": "Office", "enabled": false, "is_deletable": true,
                "action_type": { "DivertAction": {
                  "target_interface_ipv4": { "InterfaceLuid": 14918723538255872 }, "target_interface_ipv6": null,
                  "matches": [ { "RemoteIpv4Range": { "start": "10.80.90.0", "end": "10.80.90.255" } },
                               { "RemoteIpv4Subnet": "10.90.0.0/22" } ] } } } ]
            }
          ]
        }
        """;

    private const string InterfacesJson = """
        { "version": 1, "interfaces": {
            "14918723538255872": { "observed_name": "OpenVPN Connect DCO Adapter", "custom_name": null },
            "14918723555033088": { "observed_name": "AmneziaVPN", "custom_name": "Work VPN" } } }
        """;

    [Fact]
    public void Imports_groups_rules_matches_and_interface_names()
    {
        var import = RoulerImporter.Import(RulesJson, InterfacesJson);

        Assert.Equal(2, import.Groups.Count);
        var appGroup = import.Groups[0];
        Assert.False(appGroup.Enabled);
        Assert.Contains("app-specific", appGroup.Name);
        Assert.Equal("Work VPN", appGroup.Rules.Single().Ipv4Interface!.Name);
        Assert.Contains(import.Warnings, w => w.Contains("tvnviewer.exe"));

        var rule = import.Groups[1].Rules.Single();
        Assert.Equal("Office", rule.Name);
        Assert.False(rule.Enabled);
        Assert.Equal(["10.80.90.0-10.80.90.255", "10.90.0.0/22"], rule.Matches);
        Assert.Equal(new InterfaceRef(14918723538255872, "OpenVPN Connect DCO Adapter"), rule.Ipv4Interface);
        Assert.Null(rule.Ipv6Interface);
    }
}
