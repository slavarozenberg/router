using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows.Data;
using System.Windows.Threading;
using RouteManager.App.Infrastructure;
using RouteManager.Core.Net;
using RouteManager.Core.Steering;

namespace RouteManager.App.ViewModels;

/// <summary>Live TCP connections: which process talks to which address, over which interface and gateway.</summary>
public sealed class ConnectionsViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly IConnectionTable _table;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, ConnectionRow> _rowsByKey = [];
    private IReadOnlyDictionary<ulong, InterfaceInfo> _interfaces = new Dictionary<ulong, InterfaceInfo>();
    private IReadOnlyList<PlannedRoute> _planned = [];
    private IReadOnlyDictionary<Guid, string> _ruleNames = new Dictionary<Guid, string>();
    private bool _active;
    private bool _refreshing;
    private bool _paused;
    private bool _onlySteered;
    private string _filterText = "";
    private string _summary = "";

    public ConnectionsViewModel(IConnectionTable table)
    {
        _table = table;
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh();

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        view.Filter = Matches;
        view.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.Process), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.Remote), ListSortDirection.Ascending));
        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(ConnectionRow.IsSteered));
        View = view;
    }

    public ObservableCollection<ConnectionRow> Rows { get; } = [];
    public ICollectionView View { get; }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
                View.Refresh();
        }
    }

    public bool OnlySteered
    {
        get => _onlySteered;
        set
        {
            if (SetProperty(ref _onlySteered, value))
                View.Refresh();
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            if (SetProperty(ref _paused, value))
                UpdateTimer();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Starts or stops polling; the view calls this as it is shown or hidden.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        UpdateTimer();
    }

    /// <summary>Interface names and the current plan, used to label connections. Takes effect on the next refresh.</summary>
    public void UpdateContext(IReadOnlyDictionary<ulong, InterfaceInfo> interfaces, IReadOnlyList<PlannedRoute> planned,
        IReadOnlyDictionary<Guid, string> ruleNames)
    {
        _interfaces = interfaces;
        _planned = planned;
        _ruleNames = ruleNames;
    }

    private void UpdateTimer()
    {
        if (_active && !_paused)
        {
            if (!_timer.IsEnabled)
                Refresh();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private async void Refresh()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            var planned = _planned;
            var (traced, processes) = await Task.Run(() => (ConnectionTracer.Trace(_table, planned), ProcessNames()));
            Sync(traced, processes);
        }
        catch (Exception ex)
        {
            Summary = $"Could not read connections: {ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Sync(IReadOnlyList<TracedConnection> traced, IReadOnlyDictionary<int, string> processes)
    {
        var seen = new HashSet<string>();
        foreach (var item in traced)
        {
            var c = item.Connection;
            var key = $"{c.ProcessId}|{c.Local}|{c.Remote}";
            if (!seen.Add(key))
                continue;
            if (!_rowsByKey.TryGetValue(key, out var row))
            {
                var process = processes.GetValueOrDefault(c.ProcessId, "(exited)");
                row = new ConnectionRow(process, c.ProcessId, c.Local.ToString(), c.Remote.ToString());
                _rowsByKey[key] = row;
                Rows.Add(row);
            }

            var route = item.Route;
            var nic = route is null ? "—"
                : _interfaces.TryGetValue(route.InterfaceLuid, out var info) ? info.Name
                : $"LUID {route.InterfaceLuid}";
            var gateway = route is null ? "no route" : route.IsOnLink ? "on-link" : route.NextHop.ToString();
            var rule = item.RuleId is { } id ? _ruleNames.GetValueOrDefault(id, "(rule)") : "";
            row.Update(StateText(c.State), nic, gateway, route?.Destination.ToString() ?? "—", rule);
        }

        foreach (var stale in _rowsByKey.Where(kv => !seen.Contains(kv.Key)).ToList())
        {
            _rowsByKey.Remove(stale.Key);
            Rows.Remove(stale.Value);
        }

        var steered = Rows.Count(r => r.IsSteered);
        Summary = $"{Rows.Count} TCP connection(s) · {steered} through rules · updated {DateTime.Now:HH:mm:ss}";
    }

    private bool Matches(object item)
    {
        var row = (ConnectionRow)item;
        if (OnlySteered && !row.IsSteered)
            return false;
        var text = FilterText.Trim();
        return text.Length == 0
               || new[] { row.Process, row.ProcessId.ToString(), row.Remote, row.Local, row.Interface, row.Gateway, row.Route, row.Rule }
                   .Any(field => field.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<int, string> ProcessNames()
    {
        var names = new Dictionary<int, string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names[process.Id] = process.Id == 4 ? "System" : process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    // exited while we were enumerating
                }
            }
        }
        return names;
    }

    private static string StateText(TcpState state) => state switch
    {
        TcpState.Established => "Established",
        TcpState.SynSent => "Connecting",
        TcpState.SynReceived => "Accepting",
        _ => state.ToString(),
    };
}

public sealed class ConnectionRow(string process, int processId, string local, string remote) : ObservableObject
{
    private string _state = "";
    private string _interface = "";
    private string _gateway = "";
    private string _route = "";
    private string _rule = "";

    public string Process { get; } = process;
    public int ProcessId { get; } = processId;
    public string Local { get; } = local;
    public string Remote { get; } = remote;

    public string State => _state;
    public string Interface => _interface;
    public string Gateway => _gateway;
    public string Route => _route;
    public string Rule => _rule;
    public bool IsSteered => _rule.Length > 0;

    public void Update(string state, string nic, string gateway, string route, string rule)
    {
        SetProperty(ref _state, state, nameof(State));
        SetProperty(ref _interface, nic, nameof(Interface));
        SetProperty(ref _gateway, gateway, nameof(Gateway));
        SetProperty(ref _route, route, nameof(Route));
        if (SetProperty(ref _rule, rule, nameof(Rule)))
            OnPropertyChanged(nameof(IsSteered));
    }
}
