using System.Net.Sockets;
using RouteManager.App.Infrastructure;
using RouteManager.Core.Model;
using RouteManager.Core.Net;

namespace RouteManager.App.ViewModels;

public sealed record InterfaceOption(InterfaceRef? Ref, string Label)
{
    public static readonly InterfaceOption None = new(null, "None");
}

public sealed class RuleEditorViewModel : ObservableObject
{
    private readonly Rule _rule;
    private string _name;
    private bool _enabled;
    private string _matchesText;
    private InterfaceOption _ipv4;
    private InterfaceOption _ipv6;

    public RuleEditorViewModel(Rule rule, IReadOnlyList<InterfaceInfo> interfaces, bool isNew)
    {
        _rule = rule;
        Title = isNew ? "Add rule" : "Edit rule";
        _name = rule.Name ?? "";
        _enabled = rule.Enabled;
        _matchesText = string.Join(Environment.NewLine, rule.Matches);

        Ipv4Options = BuildOptions(interfaces, AddressFamily.InterNetwork, rule.Ipv4Interface);
        Ipv6Options = BuildOptions(interfaces, AddressFamily.InterNetworkV6, rule.Ipv6Interface);
        _ipv4 = Ipv4Options.First(o => o.Ref?.Luid == rule.Ipv4Interface?.Luid);
        _ipv6 = Ipv6Options.First(o => o.Ref?.Luid == rule.Ipv6Interface?.Luid);
        Validate();
    }

    public string Title { get; }
    public IReadOnlyList<InterfaceOption> Ipv4Options { get; }
    public IReadOnlyList<InterfaceOption> Ipv6Options { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string MatchesText
    {
        get => _matchesText;
        set
        {
            if (SetProperty(ref _matchesText, value))
                Validate();
        }
    }

    public InterfaceOption SelectedIpv4
    {
        get => _ipv4;
        set
        {
            if (SetProperty(ref _ipv4, value))
                Validate();
        }
    }

    public InterfaceOption SelectedIpv6
    {
        get => _ipv6;
        set
        {
            if (SetProperty(ref _ipv6, value))
                Validate();
        }
    }

    public string ValidationText { get; private set; } = "";
    public bool HasErrors { get; private set; }
    public bool HasWarnings { get; private set; }

    /// <summary>Writes the edits into the rule. Call only when <see cref="HasErrors"/> is false.</summary>
    public Rule Commit()
    {
        _rule.Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim();
        _rule.Enabled = Enabled;
        _rule.Matches = Lines().ToList();
        _rule.Ipv4Interface = SelectedIpv4.Ref;
        _rule.Ipv6Interface = SelectedIpv6.Ref;
        return _rule;
    }

    private IEnumerable<string> Lines() =>
        MatchesText.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void Validate()
    {
        var errors = new List<string>();
        var matches = new List<AddressMatch>();
        foreach (var line in Lines())
        {
            if (AddressMatch.TryParse(line, out var match, out var error))
                matches.Add(match);
            else
                errors.Add(error!);
        }

        if (matches.Count == 0 && errors.Count == 0)
            errors.Add("Enter at least one destination.");

        // A missing interface only leaves that family unrouted (the rule shows as Partial), so it doesn't block saving.
        var warnings = new List<string>();
        var v4 = matches.Where(m => m.Family == AddressFamily.InterNetwork).ToList();
        var v6 = matches.Where(m => m.Family == AddressFamily.InterNetworkV6).ToList();
        if (v4.Count > 0 && SelectedIpv4.Ref is null)
            warnings.Add("IPv4 destinations won't be routed until you choose an IPv4 interface.");
        if (v6.Count > 0 && SelectedIpv6.Ref is null)
            warnings.Add("IPv6 destinations won't be routed until you choose an IPv6 interface.");

        HasErrors = errors.Count > 0;
        HasWarnings = !HasErrors && warnings.Count > 0;
        if (HasErrors)
        {
            ValidationText = string.Join(Environment.NewLine, errors);
        }
        else
        {
            var routes = matches.SelectMany(m => m.ToPrefixes()).Distinct().Count();
            ValidationText = $"{matches.Count} destination(s) → {routes} route(s)" +
                             (v4.Count > 0 && v6.Count > 0 ? $" ({v4.Count} IPv4, {v6.Count} IPv6)." : ".") +
                             string.Concat(warnings.Select(w => Environment.NewLine + w));
        }
        OnPropertyChanged(nameof(ValidationText));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasWarnings));
    }

    private static List<InterfaceOption> BuildOptions(IReadOnlyList<InterfaceInfo> interfaces, AddressFamily family, InterfaceRef? current)
    {
        var label = family == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
        var options = new List<InterfaceOption> { InterfaceOption.None };
        foreach (var nic in interfaces.Where(i => !i.IsLoopback))
        {
            var state = !nic.IsUp ? "down"
                : !nic.HasAddress(family) ? $"no {label} address"
                : nic.GatewayFor(family) is { } gateway ? $"via {gateway}"
                : "on-link";
            options.Add(new InterfaceOption(new InterfaceRef(nic.Luid, nic.Name), $"{nic.Name}  —  {state}"));
        }
        if (current is not null && options.All(o => o.Ref?.Luid != current.Luid))
            options.Add(new InterfaceOption(current, $"{current.Name}  —  not present"));
        return options;
    }
}
