using System.Net.NetworkInformation;
using RouteManager.Core.Model;
using RouteManager.Core.Net;
using RouteManager.Core.Storage;

namespace RouteManager.Core.Steering;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

public sealed record ApplyResult(
    RoutePlan Plan,
    IReadOnlyList<InterfaceInfo> Interfaces,
    IReadOnlyList<RouteKey> OwnedRoutes,
    int Added,
    int Removed,
    IReadOnlyList<string> Errors);

/// <summary>
/// Applies a <see cref="RuleSet"/> to the system routing table and keeps it applied:
/// network or routing-table changes trigger a debounced re-apply, the way Rouler's service does.
/// </summary>
public sealed class SteeringEngine : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(800);

    private readonly IRouteTable _table;
    private readonly IInterfaceCatalog _catalog;
    private readonly LedgerStore _ledger;
    private readonly object _gate = new();
    private readonly Timer _debounceTimer;
    private RouteChangeWatcher? _watcher;
    private RuleSet _rules = new();
    private bool _canWrite;
    private Dictionary<Guid, RuleStatus> _lastStatuses = [];

    public SteeringEngine(IRouteTable table, IInterfaceCatalog catalog, LedgerStore ledger)
    {
        _table = table;
        _catalog = catalog;
        _ledger = ledger;
        _debounceTimer = new Timer(_ => ReapplyAfterChange());
    }

    /// <summary>Raised after every apply (including automatic ones), on a background thread.</summary>
    public event EventHandler<ApplyResult>? Applied;

    /// <summary>Raised for activity-log lines, on whichever thread produced them.</summary>
    public event EventHandler<LogEntry>? Logged;

    /// <summary>
    /// Sets the rules and reconciles the routing table with them. With <paramref name="canWrite"/> false
    /// (not elevated) nothing is changed; the result shows what would be routed.
    /// </summary>
    public ApplyResult Apply(RuleSet rules, bool canWrite, string reason)
    {
        lock (_gate)
        {
            _rules = rules;
            _canWrite = canWrite;
            return ApplyLocked(reason);
        }
    }

    /// <summary>Starts re-applying the current rules whenever routes, addresses or connectivity change.</summary>
    public void StartWatching()
    {
        if (_watcher is not null)
            return;
        _watcher = new RouteChangeWatcher();
        _watcher.Changed += OnNetworkChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);

    private void ReapplyAfterChange()
    {
        try
        {
            lock (_gate)
                ApplyLocked("network change");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Re-apply after network change failed: {ex.Message}");
        }
    }

    private ApplyResult ApplyLocked(string reason)
    {
        var interfaces = _catalog.GetInterfaces();
        var plan = RoutePlanner.Plan(_rules, interfaces);
        LogStatusChanges(plan);
        var ledger = _ledger.Load();
        var errors = new List<string>();

        if (!_canWrite)
        {
            var result = new ApplyResult(plan, interfaces, ledger, 0, 0, errors);
            Applied?.Invoke(this, result);
            return result;
        }

        var live = _table.GetRoutes().Select(r => r.Key).ToList();
        var actions = Reconciler.Diff(plan.Routes, ledger, live);
        var owned = actions.Keep.ToHashSet();
        int added = 0, removed = 0;

        // Add before deleting so moving a destination to another interface never leaves a gap.
        foreach (var route in actions.ToAdd)
        {
            try
            {
                if (_table.Add(route.Key, route.Metric))
                {
                    owned.Add(route.Key);
                    added++;
                }
            }
            catch (RouteTableException ex)
            {
                errors.Add(ex.Message);
            }
        }

        foreach (var key in actions.ToDelete)
        {
            try
            {
                if (_table.Delete(key))
                    removed++;
            }
            catch (RouteTableException ex)
            {
                owned.Add(key); // still ours; retry on the next apply
                errors.Add(ex.Message);
            }
        }

        _ledger.Save(owned);

        if (added > 0 || removed > 0)
            Log(LogLevel.Info, $"Rules applied ({reason}): {added} route(s) added, {removed} removed, {owned.Count} in place.");
        foreach (var error in errors)
            Log(LogLevel.Error, error);

        var applied = new ApplyResult(plan, interfaces, owned.ToList(), added, removed, errors);
        Applied?.Invoke(this, applied);
        return applied;
    }

    private void LogStatusChanges(RoutePlan plan)
    {
        var names = _rules.Groups.SelectMany(g => g.Rules).ToDictionary(r => r.Id, RoutePlanner.DisplayName);
        foreach (var (id, status) in plan.RuleStatuses)
        {
            var previous = _lastStatuses.GetValueOrDefault(id);
            if (previous is not null && previous.State == status.State && previous.Messages.SequenceEqual(status.Messages))
                continue;
            if (status.State is RuleState.Partial or RuleState.Inactive)
                Log(LogLevel.Warning, $"Rule \"{names[id]}\": {string.Join(" ", status.Messages)}");
            else if (previous is { State: RuleState.Partial or RuleState.Inactive } && status.State == RuleState.Active)
                Log(LogLevel.Info, $"Rule \"{names[id]}\" is active again.");
        }
        _lastStatuses = new Dictionary<Guid, RuleStatus>(plan.RuleStatuses);
    }

    private void Log(LogLevel level, string message) =>
        Logged?.Invoke(this, new LogEntry(DateTime.Now, level, message));

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _watcher?.Dispose();
        _debounceTimer.Dispose();
    }
}
