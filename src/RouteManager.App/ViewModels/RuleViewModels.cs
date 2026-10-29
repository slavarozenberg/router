using System.Collections.ObjectModel;
using RouteManager.App.Infrastructure;
using RouteManager.Core.Model;
using RouteManager.Core.Net;
using RouteManager.Core.Steering;

namespace RouteManager.App.ViewModels;

public sealed class GroupViewModel : ObservableObject
{
    private readonly Action _commit;

    public GroupViewModel(RuleGroup model, Action commit)
    {
        Model = model;
        _commit = commit;
        Rules = new ObservableCollection<RuleViewModel>(model.Rules.Select(r => new RuleViewModel(r, commit)));
    }

    public RuleGroup Model { get; }
    public ObservableCollection<RuleViewModel> Rules { get; }

    public string Name
    {
        get => Model.Name;
        set
        {
            Model.Name = value;
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value)
                return;
            Model.Enabled = value;
            OnPropertyChanged();
            _commit();
        }
    }

    public string Summary => Model.Rules.Count == 1 ? "1 rule" : $"{Model.Rules.Count} rules";

    public void RefreshSummary() => OnPropertyChanged(nameof(Summary));
}

public sealed class RuleViewModel : ObservableObject
{
    private readonly Action _commit;
    private RuleStatus _status = RuleStatus.Off;
    private bool _applied;
    private IReadOnlyDictionary<ulong, InterfaceInfo> _interfaces = new Dictionary<ulong, InterfaceInfo>();

    public RuleViewModel(Rule model, Action commit)
    {
        Model = model;
        _commit = commit;
    }

    public Rule Model { get; }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value)
                return;
            Model.Enabled = value;
            OnPropertyChanged();
            _commit();
        }
    }

    public string DisplayName => RoutePlanner.DisplayName(Model);
    public string Destinations => string.Join(", ", Model.Matches);
    public string Ipv4Via => Describe(Model.Ipv4Interface);
    public string Ipv6Via => Describe(Model.Ipv6Interface);

    public RuleState State => _status.State;

    public string StatusText => _status.State switch
    {
        RuleState.Off => "Off",
        RuleState.Active => $"{(_applied ? "Active" : "Planned")} · {Routes(_status.RouteCount)}",
        RuleState.Partial => $"Partial · {Routes(_status.RouteCount)}",
        _ => "Inactive",
    };

    public string StatusDetail => string.Join(Environment.NewLine, _status.Messages);

    /// <param name="applied">False when running unelevated: the plan is shown but routes are not in the table.</param>
    public void Update(RuleStatus status, IReadOnlyDictionary<ulong, InterfaceInfo> interfaces, bool applied)
    {
        _status = status;
        _interfaces = interfaces;
        _applied = applied;
        OnPropertyChanged(null);
    }

    private static string Routes(int count) => count == 1 ? "1 route" : $"{count} routes";

    /// <summary>Re-reads every displayed property after the model was edited.</summary>
    public void Refresh() => OnPropertyChanged(null);

    private string Describe(InterfaceRef? target)
    {
        if (target is null)
            return "—";
        if (!_interfaces.TryGetValue(target.Luid, out var nic))
            return $"{target.Name} (not present)";
        return nic.IsUp ? nic.Name : $"{nic.Name} (down)";
    }
}

public sealed record AppliedRouteRow(string Destination, string NextHop, string Interface);
