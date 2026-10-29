using RouteManager.Core.Net;

namespace RouteManager.Core.Steering;

public sealed record ReconcileActions(IReadOnlyList<PlannedRoute> ToAdd, IReadOnlyList<RouteKey> ToDelete, IReadOnlyList<RouteKey> Keep);

/// <summary>
/// Diffs the planned routes against the routes this app owns (the ledger) and the live table.
/// Only ledger routes are ever deleted, so routes created by the user, Windows or VPN clients are never touched.
/// A planned route that already exists but is not in the ledger is left alone and not adopted.
/// </summary>
public static class Reconciler
{
    public static ReconcileActions Diff(
        IReadOnlyList<PlannedRoute> planned, IReadOnlyCollection<RouteKey> ledger, IReadOnlyCollection<RouteKey> live)
    {
        var plannedKeys = planned.Select(p => p.Key).ToHashSet();
        var liveSet = live.ToHashSet();
        var ledgerSet = ledger.ToHashSet();

        var toAdd = planned.Where(p => !liveSet.Contains(p.Key)).ToList();
        var keep = ledgerSet.Where(k => plannedKeys.Contains(k) && liveSet.Contains(k)).ToList();
        var toDelete = ledgerSet.Where(k => !plannedKeys.Contains(k)).ToList();
        return new ReconcileActions(toAdd, toDelete, keep);
    }
}
