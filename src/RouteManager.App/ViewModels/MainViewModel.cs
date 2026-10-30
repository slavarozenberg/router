using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using RouteManager.App.Infrastructure;
using RouteManager.Core.Model;
using RouteManager.Core.Net;
using RouteManager.Core.Steering;
using RouteManager.Core.Storage;

namespace RouteManager.App.ViewModels;

public enum ImportMode
{
    Add,
    Replace,
}

/// <summary>Window-specific interactions the view model asks the view to perform.</summary>
public interface IDialogs
{
    bool EditRule(RuleEditorViewModel editor);
    string? PromptText(string title, string label, string initial);
    bool Confirm(string message);
    void Show(string message, bool isWarning = false);
    string? PickImportFile();
    string? PickExportFile(string suggestedName);
    ImportMode? AskImportMode(string message);
}

public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogEntries = 500;

    private readonly RuleStore _store;
    private readonly SteeringEngine _engine;
    private readonly IDialogs _dialogs;
    private readonly RuleSet _rules;
    private IReadOnlyList<InterfaceInfo> _interfaces = [];
    private IReadOnlyDictionary<ulong, InterfaceInfo> _interfacesByLuid = new Dictionary<ulong, InterfaceInfo>();
    private RoutePlan? _lastPlan;
    private GroupViewModel? _selectedGroup;
    private RuleViewModel? _selectedRule;
    private string _statusText = "Loading…";

    public MainViewModel(RuleStore store, SteeringEngine engine, IConnectionTable connections, IDialogs dialogs)
    {
        _store = store;
        _engine = engine;
        _dialogs = dialogs;
        _rules = store.Load();
        Groups = new ObservableCollection<GroupViewModel>(_rules.Groups.Select(NewGroupViewModel));
        _selectedGroup = Groups.FirstOrDefault();
        Connections = new ConnectionsViewModel(connections);

        _engine.Applied += (_, result) => OnUi(() => OnApplied(result));
        _engine.Logged += (_, entry) => OnUi(() => AddLog(entry));

        AddGroupCommand = new RelayCommand(AddGroup);
        RenameGroupCommand = new RelayCommand(RenameGroup, () => SelectedGroup is not null);
        DeleteGroupCommand = new RelayCommand(DeleteGroup, () => SelectedGroup is not null);
        AddRuleCommand = new RelayCommand(AddRule, () => SelectedGroup is not null);
        EditRuleCommand = new RelayCommand(EditRule, () => SelectedRule is not null);
        DuplicateRuleCommand = new RelayCommand(DuplicateRule, () => SelectedRule is not null);
        DeleteRuleCommand = new RelayCommand(DeleteRule, () => SelectedRule is not null);
        MoveRuleUpCommand = new RelayCommand(() => MoveRule(-1), () => CanMoveRule(-1));
        MoveRuleDownCommand = new RelayCommand(() => MoveRule(+1), () => CanMoveRule(+1));
        ApplyNowCommand = new RelayCommand(() => Apply("manual apply"));
        ImportCommand = new RelayCommand(Import);
        ExportCommand = new RelayCommand(Export, () => _rules.Groups.Count > 0);
        RestartElevatedCommand = new RelayCommand(RestartElevated, () => !IsElevated);
    }

    public ObservableCollection<GroupViewModel> Groups { get; }
    public ObservableCollection<LogEntry> Log { get; } = [];
    public ObservableCollection<AppliedRouteRow> AppliedRoutes { get; } = [];
    public ConnectionsViewModel Connections { get; }

    public bool IsElevated => Elevation.IsElevated;
    public bool ShowElevationBanner => !IsElevated;

    public ICommand AddGroupCommand { get; }
    public ICommand RenameGroupCommand { get; }
    public ICommand DeleteGroupCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand EditRuleCommand { get; }
    public ICommand DuplicateRuleCommand { get; }
    public ICommand DeleteRuleCommand { get; }
    public ICommand MoveRuleUpCommand { get; }
    public ICommand MoveRuleDownCommand { get; }
    public ICommand ApplyNowCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand RestartElevatedCommand { get; }

    public bool SteeringEnabled
    {
        get => _rules.SteeringEnabled;
        set
        {
            if (_rules.SteeringEnabled == value)
                return;
            _rules.SteeringEnabled = value;
            OnPropertyChanged();
            Commit();
        }
    }

    public GroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
                SelectedRule = null;
        }
    }

    public RuleViewModel? SelectedRule
    {
        get => _selectedRule;
        set => SetProperty(ref _selectedRule, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>First apply after launch: restores routes for enabled rules and starts following network changes.</summary>
    public void Start()
    {
        if (!IsElevated)
            AddLog(new LogEntry(DateTime.Now, LogLevel.Warning,
                "Not running as administrator: rules can be edited, but routes are not changed."));
        Apply("startup");
        _engine.StartWatching();
    }

    /// <summary>Removes the app's routes before exit. Returns how many were removed.</summary>
    public int RemoveRoutesForExit()
    {
        if (!IsElevated)
            return 0;
        var stopped = _rules.Clone();
        stopped.SteeringEnabled = false;
        return _engine.Apply(stopped, canWrite: true, "exit").Removed;
    }

    private void Commit()
    {
        try
        {
            _store.Save(_rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog(new LogEntry(DateTime.Now, LogLevel.Error, $"Could not save rules: {ex.Message}"));
        }
        foreach (var group in Groups)
            group.RefreshSummary();
        Apply("rules changed");
    }

    private void Apply(string reason)
    {
        var snapshot = _rules.Clone();
        Task.Run(() =>
        {
            try
            {
                _engine.Apply(snapshot, IsElevated, reason);
            }
            catch (Exception ex)
            {
                OnUi(() => AddLog(new LogEntry(DateTime.Now, LogLevel.Error, $"Apply failed: {ex.Message}")));
            }
        });
    }

    private void OnApplied(ApplyResult result)
    {
        _interfaces = result.Interfaces;
        _interfacesByLuid = result.Interfaces.DistinctBy(i => i.Luid).ToDictionary(i => i.Luid);
        _lastPlan = result.Plan;
        RefreshStatuses();
        var ruleNames = Groups.SelectMany(g => g.Rules).ToDictionary(r => r.Model.Id, r => r.DisplayName);
        Connections.UpdateContext(_interfacesByLuid, result.Plan.Routes, ruleNames);

        AppliedRoutes.Clear();
        var routes = IsElevated ? result.OwnedRoutes : result.Plan.Routes.Select(r => r.Key).ToList();
        foreach (var key in routes.OrderBy(k => k.Destination.Family).ThenBy(k => k.Destination.ToString()))
        {
            var name = _interfacesByLuid.TryGetValue(key.InterfaceLuid, out var nic) ? nic.Name : $"LUID {key.InterfaceLuid}";
            AppliedRoutes.Add(new AppliedRouteRow(key.Destination.ToString(), key.IsOnLink ? "on-link" : key.NextHop.ToString(), name));
        }

        var statuses = result.Plan.RuleStatuses.Values;
        var on = statuses.Count(s => s.State != RuleState.Off);
        var active = statuses.Count(s => s.State == RuleState.Active);
        var problems = statuses.Count(s => s.State is RuleState.Partial or RuleState.Inactive);
        StatusText = !_rules.SteeringEnabled ? "Steering is off — no routes are applied."
            : !IsElevated ? $"Read-only (not administrator): {result.Plan.Routes.Count} route(s) would be applied."
            : $"{active} of {on} rule(s) active · {result.OwnedRoutes.Count} route(s) in place" +
              (problems > 0 ? $" · {problems} rule(s) need attention" : "");
    }

    private void RefreshStatuses()
    {
        foreach (var rule in Groups.SelectMany(g => g.Rules))
            rule.Update(_lastPlan?.RuleStatuses.GetValueOrDefault(rule.Model.Id) ?? RuleStatus.Off, _interfacesByLuid, IsElevated);
    }

    private void AddLog(LogEntry entry)
    {
        Log.Insert(0, entry);
        while (Log.Count > MaxLogEntries)
            Log.RemoveAt(Log.Count - 1);
    }

    private GroupViewModel NewGroupViewModel(RuleGroup group) => new(group, Commit);

    private void AddGroup()
    {
        var name = _dialogs.PromptText("Add group", "Group name:", "New group");
        if (string.IsNullOrWhiteSpace(name))
            return;
        var group = new RuleGroup { Name = name.Trim() };
        _rules.Groups.Add(group);
        var vm = NewGroupViewModel(group);
        Groups.Add(vm);
        SelectedGroup = vm;
        Commit();
    }

    private void RenameGroup()
    {
        var group = SelectedGroup!;
        var name = _dialogs.PromptText("Rename group", "Group name:", group.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == group.Name)
            return;
        group.Name = name.Trim();
        Commit();
    }

    private void DeleteGroup()
    {
        var group = SelectedGroup!;
        var detail = group.Rules.Count > 0 ? $" and its {group.Summary}" : "";
        if (!_dialogs.Confirm($"Delete group \"{group.Name}\"{detail}? Their routes will be removed."))
            return;
        _rules.Groups.Remove(group.Model);
        Groups.Remove(group);
        SelectedGroup = Groups.FirstOrDefault();
        Commit();
    }

    private void AddRule()
    {
        var rule = new Rule();
        var editor = new RuleEditorViewModel(rule, _interfaces, isNew: true);
        if (!_dialogs.EditRule(editor))
            return;
        editor.Commit();

        var group = SelectedGroup!;
        group.Model.Rules.Add(rule);
        var vm = new RuleViewModel(rule, Commit);
        group.Rules.Add(vm);
        SelectedRule = vm;
        Commit();
    }

    private void EditRule()
    {
        var rule = SelectedRule!;
        var editor = new RuleEditorViewModel(rule.Model.Clone(), _interfaces, isNew: false);
        if (!_dialogs.EditRule(editor))
            return;
        ReplaceRule(rule, editor.Commit());
    }

    private void DuplicateRule()
    {
        var copy = SelectedRule!.Model.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = copy.Name is null ? null : $"{copy.Name} (copy)";
        var editor = new RuleEditorViewModel(copy, _interfaces, isNew: true);
        if (!_dialogs.EditRule(editor))
            return;
        editor.Commit();

        var group = SelectedGroup!;
        var index = group.Rules.IndexOf(SelectedRule!) + 1;
        group.Model.Rules.Insert(index, copy);
        var vm = new RuleViewModel(copy, Commit);
        group.Rules.Insert(index, vm);
        SelectedRule = vm;
        Commit();
    }

    private void ReplaceRule(RuleViewModel old, Rule updated)
    {
        var group = SelectedGroup!;
        var index = group.Rules.IndexOf(old);
        group.Model.Rules[index] = updated;
        var vm = new RuleViewModel(updated, Commit);
        group.Rules[index] = vm;
        SelectedRule = vm;
        Commit();
    }

    private void DeleteRule()
    {
        var rule = SelectedRule!;
        if (!_dialogs.Confirm($"Delete rule \"{rule.DisplayName}\"? Its routes will be removed."))
            return;
        var group = SelectedGroup!;
        group.Model.Rules.Remove(rule.Model);
        group.Rules.Remove(rule);
        Commit();
    }

    private bool CanMoveRule(int offset)
    {
        if (SelectedGroup is null || SelectedRule is null)
            return false;
        var target = SelectedGroup.Rules.IndexOf(SelectedRule) + offset;
        return target >= 0 && target < SelectedGroup.Rules.Count;
    }

    private void MoveRule(int offset)
    {
        var group = SelectedGroup!;
        var rule = SelectedRule!;
        var index = group.Rules.IndexOf(rule);
        group.Model.Rules.RemoveAt(index);
        group.Model.Rules.Insert(index + offset, rule.Model);
        group.Rules.Move(index, index + offset);
        SelectedRule = rule;
        Commit();
    }

    private void Export()
    {
        var path = _dialogs.PickExportFile($"route-manager-rules-{DateTime.Now:yyyy-MM-dd}.json");
        if (path is null)
            return;
        try
        {
            RuleTransfer.Export(path, _rules.Groups);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Show($"Could not export to \"{path}\":\n{ex.Message}", isWarning: true);
            return;
        }
        var ruleCount = _rules.Groups.Sum(g => g.Rules.Count);
        AddLog(new LogEntry(DateTime.Now, LogLevel.Info, $"Exported {_rules.Groups.Count} group(s) with {ruleCount} rule(s) to {path}."));
    }

    private void Import()
    {
        var path = _dialogs.PickImportFile();
        if (path is null)
            return;

        RuleImport import;
        try
        {
            import = RuleTransfer.ImportFile(path, _interfaces);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _dialogs.Show($"Could not import \"{path}\":\n{ex.Message}", isWarning: true);
            return;
        }

        var ruleCount = import.Groups.Sum(g => g.Rules.Count);
        if (import.Groups.Count == 0)
        {
            _dialogs.Show($"\"{path}\" contains no rule groups.", isWarning: true);
            return;
        }
        var mode = _dialogs.AskImportMode(
            $"The file has {import.Groups.Count} group(s) with {ruleCount} rule(s).\n\n" +
            "Yes: replace all current groups and rules (their routes are removed).\n" +
            "No: add the imported groups to the current ones.");
        if (mode is null)
            return;

        if (mode == ImportMode.Replace)
        {
            _rules.Groups.Clear();
            Groups.Clear();
        }
        foreach (var group in import.Groups)
        {
            _rules.Groups.Add(group);
            Groups.Add(NewGroupViewModel(group));
        }
        SelectedGroup = mode == ImportMode.Replace ? Groups.FirstOrDefault() : Groups.LastOrDefault();
        Commit();

        var verb = mode == ImportMode.Replace ? "Replaced the rules with" : "Imported";
        AddLog(new LogEntry(DateTime.Now, LogLevel.Info, $"{verb} {import.Groups.Count} group(s) with {ruleCount} rule(s) from {path}."));
        if (import.Warnings.Count > 0)
            _dialogs.Show(string.Join("\n\n", import.Warnings), isWarning: true);
    }

    private void RestartElevated()
    {
        if (Elevation.TryRestartElevated())
            Application.Current.Shutdown();
    }

    private static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
