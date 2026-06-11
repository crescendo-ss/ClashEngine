using System.Collections.Generic;
using ClashEngine.Core.Queue;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Pure computation behind the orchestrator's <c>?return</c> items policy
/// (<see cref="QueueDefinition.ReturnItemsAction"/>): given the ship's spawn-fresh initial
/// loadout and the player's saved last-leave inventory, produce the per-item count adjustments
/// (positive = grant, negative = take away) the adapter should apply to the freshly placed ship.
/// </summary>
public static class ReturnItemsPlan
{
    /// <summary>
    /// Compute the adjustments for one return placement.
    /// <list type="bullet">
    /// <item><see cref="ItemsAction.Full"/>: no adjustments -- the fresh loadout stands.</item>
    /// <item><see cref="ItemsAction.Burn"/>: every item in <paramref name="initial"/> is zeroed;
    /// <paramref name="savedAtLeave"/> is ignored.</item>
    /// <item><see cref="ItemsAction.Restore"/>: reconcile the fresh loadout to
    /// <paramref name="savedAtLeave"/> over the union of both key sets, so items accumulated past
    /// the initial loadout (green pickups) are re-granted and unspent initial items are kept.</item>
    /// </list>
    /// Restore with no snapshot (<paramref name="savedAtLeave"/> is <see langword="null"/>)
    /// deliberately yields NO adjustments -- i.e. degrades to Full, not Burn. A missing snapshot
    /// means we never saw what the player left with (e.g. they exited the arena through a path
    /// that captured nothing), and reconciling toward an all-zero target would strip the entire
    /// fresh loadout -- a harsher outcome than the game type asked for.
    /// </summary>
    public static IReadOnlyList<(ItemKind Item, int Delta)> ComputeAdjustments(
        ItemsAction action,
        IReadOnlyDictionary<ItemKind, int> initial,
        IReadOnlyDictionary<ItemKind, int>? savedAtLeave)
    {
        if (action == ItemsAction.Full) return [];
        if (action == ItemsAction.Restore && savedAtLeave is null) return [];

        var saved = action == ItemsAction.Restore ? savedAtLeave : null;

        var items = new HashSet<ItemKind>(initial.Keys);
        if (saved is not null)
            foreach (var item in saved.Keys) items.Add(item);

        var result = new List<(ItemKind, int)>();
        foreach (var item in items)
        {
            int current = initial.TryGetValue(item, out var c) ? c : 0;
            int target = saved is not null && saved.TryGetValue(item, out var s) ? s : 0;
            if (current != target) result.Add((item, target - current));
        }
        return result;
    }
}
