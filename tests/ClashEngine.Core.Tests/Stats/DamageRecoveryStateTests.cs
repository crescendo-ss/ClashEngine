using ClashEngine.Core.Identity;
using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class DamageRecoveryStateTests
{
    private static PlayerKey K(string n) => new(n);

    private static DamageEntry D(uint tick, short amount, string attacker = "A", uint emp = 0) =>
        new(tick, amount, WeaponKind.Bullet, emp, K(attacker));

    [Fact]
    public void New_state_is_empty()
    {
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        Assert.Equal(0, s.Count);
        Assert.Empty(s.Snapshot(currentTick: 0));
    }

    [Fact]
    public void Single_damage_with_zero_recharge_stays_at_full_amount()
    {
        var s = new DamageRecoveryState(rechargeRate: 0.0);
        s.OnDamage(D(100, 50));
        var snap = s.Snapshot(currentTick: 1000);
        Assert.Single(snap);
        Assert.Equal(50.0, snap[0].Amount, precision: 9);
    }

    [Fact]
    public void Recovery_decrements_head_proportional_to_elapsed_ticks()
    {
        // Recharge rate = 1 damage point per tick. 50 damage at tick 100, snapshot at tick 130.
        // 30 ticks of recharge → 30 points recovered → 20 remains.
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 50));
        var snap = s.Snapshot(currentTick: 130);
        Assert.Single(snap);
        Assert.Equal(20.0, snap[0].Amount, precision: 9);
    }

    [Fact]
    public void Entry_fully_recovered_is_removed()
    {
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 50));
        // 60 ticks of recharge → 60 ≥ 50 → entry retired.
        var snap = s.Snapshot(currentTick: 160);
        Assert.Empty(snap);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Recovery_applies_FIFO_to_oldest_first()
    {
        // Two entries; recharge enough to retire first and partially eat second.
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 30, "A"));
        s.OnDamage(D(150, 40, "B"));
        // Between t=100 and t=150 (50 ticks), 50 points recovered: A's 30 retired, B's 40
        // hasn't entered the window yet — B is added at t=150 with full 40.
        // Then snapshot at t=200: 50 more ticks of recharge → 50 points → B's 40 retired,
        // surplus 10 discarded (queue empty).
        var snap = s.Snapshot(currentTick: 200);
        Assert.Empty(snap);
    }

    [Fact]
    public void Partial_recovery_across_two_entries_rolls_over()
    {
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 30, "A"));
        // Snapshot at 145: 45 ticks of recharge applied to entry A (30 retired), surplus 15 discarded.
        // Then add B at 145.
        // At snapshot 145 we just see B with 40 (A is gone).
        s.OnDamage(D(145, 40, "B"));
        var snap = s.Snapshot(currentTick: 145);
        Assert.Single(snap);
        Assert.Equal(K("B"), snap[0].Attacker);
        Assert.Equal(40.0, snap[0].Amount, precision: 9);
    }

    [Fact]
    public void Surplus_recovery_when_queue_empties_is_discarded()
    {
        // 50 damage, then 200 ticks at rate 1.0 → 200 - 50 = 150 surplus is wasted (player at full).
        // Adding new damage afterward doesn't pre-emptively eat into it.
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 50));
        s.OnDamage(D(300, 40)); // queue empty by now (200 ticks of recharge)
        var snap = s.Snapshot(currentTick: 300);
        Assert.Single(snap);
        Assert.Equal(40.0, snap[0].Amount, precision: 9);
    }

    [Fact]
    public void Emp_suspends_recovery_during_shutdown()
    {
        // EMP for 50 ticks at t=100. No recharge during [100, 150]. Snapshot at t=150 sees full 80.
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 80, emp: 50));
        var snap = s.Snapshot(currentTick: 150);
        Assert.Single(snap);
        Assert.Equal(80.0, snap[0].Amount, precision: 9);
    }

    [Fact]
    public void Emp_recovery_resumes_after_timer_expires()
    {
        // EMP for 50 ticks at t=100. Snapshot at t=200 — recharge from t=150..200 = 50 ticks.
        // 50 points recovered out of 80 → 30 remains.
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 80, emp: 50));
        var snap = s.Snapshot(currentTick: 200);
        Assert.Single(snap);
        Assert.Equal(30.0, snap[0].Amount, precision: 9);
    }

    [Fact]
    public void Stronger_emp_extends_timer_weaker_is_absorbed()
    {
        // First EMP at t=100 for 100 ticks → empEnd = 200.
        // Second EMP at t=120 for 30 ticks → would end at 150 < 200, so no change.
        // Recovery stays suspended until t=200.
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 50, emp: 100));
        s.OnDamage(D(120, 40, emp: 30));
        Assert.Equal(200u, s.EmpEndTick);

        // Snapshot at 200: no recharge. Two entries unchanged (50 + 40).
        var snap = s.Snapshot(currentTick: 200);
        Assert.Equal(2, snap.Count);
        Assert.Equal(50.0, snap[0].Amount, precision: 9);
        Assert.Equal(40.0, snap[1].Amount, precision: 9);
    }

    [Fact]
    public void Stronger_emp_overrides_a_weaker_active_one()
    {
        // First EMP at t=100 for 30 ticks → empEnd = 130.
        // Second EMP at t=110 for 200 ticks → would end at 310 > 130, so empEnd extends to 310.
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 50, emp: 30));
        s.OnDamage(D(110, 40, emp: 200));
        Assert.Equal(310u, s.EmpEndTick);
    }

    [Fact]
    public void Capacity_bound_evicts_oldest_at_limit()
    {
        var s = new DamageRecoveryState(rechargeRate: 0.0, capacity: 3);
        s.OnDamage(D(10, 50, "A"));
        s.OnDamage(D(20, 50, "B"));
        s.OnDamage(D(30, 50, "C"));
        s.OnDamage(D(40, 50, "D")); // evicts A
        var snap = s.Snapshot(currentTick: 40);
        Assert.Equal(3, snap.Count);
        Assert.Equal(new[] { K("B"), K("C"), K("D") }, snap.Select(e => e.Attacker));
    }

    [Fact]
    public void Clear_removes_entries_but_leaves_emp_timer()
    {
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 50, emp: 100));
        s.Clear();
        Assert.Equal(0, s.Count);
        Assert.Equal(200u, s.EmpEndTick); // EMP timer is a player-state property, not per-entry
    }

    [Fact]
    public void Zero_amount_event_is_ignored()
    {
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(new DamageEntry(100, Amount: 0, WeaponKind.Bullet, EmpDurationTicks: 0, K("A")));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void Snapshot_advances_state_so_subsequent_calls_see_consistent_state()
    {
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 50));
        s.Snapshot(currentTick: 130); // recovers 30, leaves 20
        var snap = s.Snapshot(currentTick: 130); // same tick; should be unchanged
        Assert.Equal(20.0, snap[0].Amount, precision: 9);
    }

    [Fact]
    public void Set_recharge_rate_applies_old_rate_up_to_now_then_new_rate_after()
    {
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 100));
        s.SetRechargeRate(2.0, currentTick: 150);  // 50 ticks @ 1.0 → 50 recovered, 50 remains
        var snap = s.Snapshot(currentTick: 175);   // 25 ticks @ 2.0 → 50 recovered, 0 remains
        Assert.Empty(snap);
    }

    [Fact]
    public void Out_of_order_event_does_not_run_clock_backwards()
    {
        // OnDamage at tick 100, then OnDamage at tick 80 (defensive — shouldn't happen but
        // we don't want it to retroactively un-recover anything).
        var s = new DamageRecoveryState(rechargeRate: 1.0);
        s.OnDamage(D(100, 30));
        s.OnDamage(D(80, 40));   // out-of-order: clock stays at 100
        Assert.Equal(100u, s.LastUpdateTick);
        var snap = s.Snapshot(currentTick: 100);
        Assert.Equal(2, snap.Count); // both entries appended
    }

    [Fact]
    public void Recharge_rate_must_be_non_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DamageRecoveryState(rechargeRate: -1.0));
    }

    [Fact]
    public void Capacity_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DamageRecoveryState(rechargeRate: 1.0, capacity: 0));
    }
}
