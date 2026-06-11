using ClashEngine.Core.Queue;
using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class ReturnItemsPlanTests
{
    private static Dictionary<ItemKind, int> Inv(params (ItemKind Item, int Count)[] items)
    {
        var d = new Dictionary<ItemKind, int>();
        foreach (var (item, count) in items) d[item] = count;
        return d;
    }

    private static Dictionary<ItemKind, int> AsMap(IReadOnlyList<(ItemKind Item, int Delta)> deltas)
    {
        var d = new Dictionary<ItemKind, int>();
        foreach (var (item, delta) in deltas) d[item] = delta;
        return d;
    }

    [Fact]
    public void Full_adjusts_nothing()
    {
        var initial = Inv((ItemKind.Burst, 2), (ItemKind.Repel, 3));
        var saved = Inv((ItemKind.Burst, 1));
        Assert.Empty(ReturnItemsPlan.ComputeAdjustments(ItemsAction.Full, initial, saved));
    }

    [Fact]
    public void Burn_zeroes_every_initial_item()
    {
        var initial = Inv((ItemKind.Burst, 2), (ItemKind.Repel, 3));
        var deltas = AsMap(ReturnItemsPlan.ComputeAdjustments(ItemsAction.Burn, initial, savedAtLeave: null));
        Assert.Equal(2, deltas.Count);
        Assert.Equal(-2, deltas[ItemKind.Burst]);
        Assert.Equal(-3, deltas[ItemKind.Repel]);
    }

    [Fact]
    public void Burn_ignores_saved_snapshot()
    {
        var initial = Inv((ItemKind.Burst, 2));
        var saved = Inv((ItemKind.Burst, 1), (ItemKind.Thor, 5));
        var deltas = AsMap(ReturnItemsPlan.ComputeAdjustments(ItemsAction.Burn, initial, saved));
        Assert.Equal(-2, deltas[ItemKind.Burst]);
        Assert.False(deltas.ContainsKey(ItemKind.Thor));
    }

    [Fact]
    public void Restore_deducts_fresh_loadout_down_to_saved_counts()
    {
        var initial = Inv((ItemKind.Burst, 2), (ItemKind.Repel, 3));
        var saved = Inv((ItemKind.Burst, 1), (ItemKind.Repel, 1));
        var deltas = AsMap(ReturnItemsPlan.ComputeAdjustments(ItemsAction.Restore, initial, saved));
        Assert.Equal(-1, deltas[ItemKind.Burst]);
        Assert.Equal(-2, deltas[ItemKind.Repel]);
    }

    [Fact]
    public void Restore_grants_items_accumulated_past_the_initial_loadout()
    {
        // Green-pickup thors on a ship whose initial Thor count is 0.
        var initial = Inv((ItemKind.Burst, 2));
        var saved = Inv((ItemKind.Burst, 2), (ItemKind.Thor, 2));
        var deltas = AsMap(ReturnItemsPlan.ComputeAdjustments(ItemsAction.Restore, initial, saved));
        Assert.Equal(2, deltas[ItemKind.Thor]);
        Assert.False(deltas.ContainsKey(ItemKind.Burst));
    }

    [Fact]
    public void Restore_zeroes_initial_items_the_player_had_spent()
    {
        // Saved snapshot omits zero-count items entirely (CaptureLastLeaveInventory only stores
        // positive counts), so an absent key means "had none" and the fresh grant is removed.
        var initial = Inv((ItemKind.Burst, 2), (ItemKind.Repel, 3));
        var saved = Inv((ItemKind.Repel, 3));
        var deltas = AsMap(ReturnItemsPlan.ComputeAdjustments(ItemsAction.Restore, initial, saved));
        Assert.Equal(-2, deltas[ItemKind.Burst]);
        Assert.False(deltas.ContainsKey(ItemKind.Repel));
    }

    [Fact]
    public void Restore_with_matching_counts_adjusts_nothing()
    {
        var initial = Inv((ItemKind.Burst, 2), (ItemKind.Repel, 3));
        var saved = Inv((ItemKind.Burst, 2), (ItemKind.Repel, 3));
        Assert.Empty(ReturnItemsPlan.ComputeAdjustments(ItemsAction.Restore, initial, saved));
    }

    [Fact]
    public void Restore_without_snapshot_degrades_to_full_not_burn()
    {
        // Regression: a player who left the arena through a path that captured no leave
        // snapshot must NOT have their fresh loadout reconciled toward an all-zero target.
        var initial = Inv((ItemKind.Burst, 2), (ItemKind.Repel, 3));
        Assert.Empty(ReturnItemsPlan.ComputeAdjustments(ItemsAction.Restore, initial, savedAtLeave: null));
    }

    [Fact]
    public void Restore_with_empty_snapshot_burns_everything()
    {
        // An EMPTY snapshot is a real capture ("player had nothing left") and is distinct from
        // a missing one -- the all-zero reconcile is correct here.
        var initial = Inv((ItemKind.Burst, 2));
        var deltas = AsMap(ReturnItemsPlan.ComputeAdjustments(ItemsAction.Restore, initial, Inv()));
        Assert.Equal(-2, deltas[ItemKind.Burst]);
    }
}
