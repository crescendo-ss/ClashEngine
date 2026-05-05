using ClashEngine.Core.Identity;
using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class StatsRecorderTests
{
    private static PlayerKey K(string n) => new(n);

    private static WeaponEnergyConfig SimpleEnergy() => new(
        new Dictionary<WeaponKind, (int, int)>
        {
            [WeaponKind.Bullet] = (100, 50),
            [WeaponKind.Bomb] = (500, 0),
            [WeaponKind.Mine] = (1000, 0),
        },
        multifireBulletEnergy: 30);

    /// <summary>Two-team registration: A,B on team 0; C,D on team 1. Max energy = 1000 each.</summary>
    private static StatsRecorder Make4Player(double recharge = 0.0, uint atTick = 0)
    {
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        var energy = SimpleEnergy();
        r.RegisterPlayer(K("A"), 0, 1000, recharge, energy, atTick);
        r.RegisterPlayer(K("B"), 0, 1000, recharge, energy, atTick);
        r.RegisterPlayer(K("C"), 1, 1000, recharge, energy, atTick);
        r.RegisterPlayer(K("D"), 1, 1000, recharge, energy, atTick);
        return r;
    }

    // --- registration ---

    [Fact]
    public void Register_creates_player_stats()
    {
        var r = Make4Player();
        Assert.Equal(4, r.Stats.Count);
        Assert.Equal(K("A"), r.Stats[K("A")].Player);
    }

    [Fact]
    public void Register_twice_throws()
    {
        var r = new StatsRecorder(new DamageDecay());
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), atTick: 0);
        Assert.Throws<InvalidOperationException>(() =>
            r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), atTick: 0));
    }

    // --- damage routing ---

    [Fact]
    public void Enemy_damage_routes_to_damage_dealt_and_taken()
    {
        var r = Make4Player();
        r.OnDamage(victim: K("C"), attacker: K("A"), amount: 200, WeaponKind.Bullet, empDurationTicks: 0, atTick: 100);
        Assert.Equal(200, r.Stats[K("A")].DamageDealt);
        Assert.Equal(200, r.Stats[K("C")].DamageTaken);
        Assert.Equal(0, r.Stats[K("A")].TeamDamageDealt);
    }

    [Fact]
    public void Team_damage_routes_to_team_counters_only()
    {
        var r = Make4Player();
        r.OnDamage(victim: K("B"), attacker: K("A"), amount: 200, WeaponKind.Bomb, 0, atTick: 100);
        Assert.Equal(0, r.Stats[K("A")].DamageDealt);
        Assert.Equal(0, r.Stats[K("B")].DamageTaken);
        Assert.Equal(200, r.Stats[K("A")].TeamDamageDealt);
        Assert.Equal(200, r.Stats[K("B")].TeamDamageTaken);
    }

    [Fact]
    public void Self_damage_routes_to_self_counter_and_does_not_enter_recovery()
    {
        var r = Make4Player();
        r.OnDamage(victim: K("A"), attacker: K("A"), amount: 50, WeaponKind.Bomb, 0, atTick: 100);
        Assert.Equal(50, r.Stats[K("A")].SelfDamage);
        Assert.Equal(0, r.Stats[K("A")].DamageTaken);
        Assert.Equal(0, r.Stats[K("A")].Recovery.Count);
    }

    [Fact]
    public void Enemy_damage_appends_to_victim_recovery_state()
    {
        var r = Make4Player();
        r.OnDamage(K("C"), K("A"), 200, WeaponKind.Bullet, 0, 100);
        var snap = r.Stats[K("C")].Recovery.Snapshot(currentTick: 100);
        Assert.Single(snap);
        Assert.Equal(K("A"), snap[0].Attacker);
    }

    [Fact]
    public void Splash_weapon_increments_attacker_hit_count_per_damage_event()
    {
        // One bomb that splashes A and B should yield two damage events → two hits on the bomb counter.
        var r = Make4Player();
        r.OnDamage(K("A"), K("C"), 100, WeaponKind.Bomb, 0, 100);
        r.OnDamage(K("B"), K("C"), 100, WeaponKind.Bomb, 0, 100);
        Assert.Equal(2, r.Stats[K("C")].PerWeapon[WeaponKind.Bomb].HitCount);
    }

    [Fact]
    public void Untracked_weapon_does_not_increment_per_weapon()
    {
        var r = Make4Player();
        r.OnDamage(K("C"), K("A"), 50, WeaponKind.Shrapnel, 0, 100);
        r.OnDamage(K("C"), K("A"), 50, WeaponKind.Burst, 0, 100);
        Assert.Empty(r.Stats[K("A")].PerWeapon);
        Assert.Equal(100, r.Stats[K("A")].DamageDealt); // raw still credited
    }

    // --- weapon fired ---

    [Fact]
    public void Weapon_fire_records_shot_and_energy_at_level_one()
    {
        var r = Make4Player();
        r.OnWeaponFired(K("A"), WeaponKind.Bullet, level: 1, multifire: false, atTick: 100);
        var stats = r.Stats[K("A")].PerWeapon[WeaponKind.Bullet];
        Assert.Equal(1, stats.FireCount);
        Assert.Equal(100, stats.EnergySpent);
    }

    [Fact]
    public void Weapon_fire_uses_multifire_cost_ladder_when_multifire_set()
    {
        var r = Make4Player();
        r.OnWeaponFired(K("A"), WeaponKind.Bullet, level: 3, multifire: true, atTick: 100);
        // Multifire is a separate projectile: 3 * 30 = 90. Bullet base/upgrade is bypassed.
        Assert.Equal(90, r.Stats[K("A")].PerWeapon[WeaponKind.Bullet].EnergySpent);
    }

    [Fact]
    public void Weapon_fire_uses_bullet_cost_ladder_when_multifire_clear()
    {
        var r = Make4Player();
        r.OnWeaponFired(K("A"), WeaponKind.Bullet, level: 3, multifire: false, atTick: 100);
        // Single-fire: 100 + (3-1)*50 = 200.
        Assert.Equal(200, r.Stats[K("A")].PerWeapon[WeaponKind.Bullet].EnergySpent);
    }

    [Fact]
    public void Weapon_fire_is_ignored_for_untracked_kinds()
    {
        var r = Make4Player();
        r.OnWeaponFired(K("A"), WeaponKind.Burst, 1, false, 100);
        r.OnWeaponFired(K("A"), WeaponKind.Shrapnel, 1, false, 100);
        Assert.Empty(r.Stats[K("A")].PerWeapon);
    }

    // --- kill flow ---

    [Fact]
    public void Kill_credits_killer_and_records_victim_death()
    {
        var r = Make4Player();
        r.OnSpawn(K("C"), atTick: 0);
        r.OnDamage(K("C"), K("A"), 800, WeaponKind.Bullet, 0, atTick: 100);
        r.OnKill(victim: K("C"), killer: K("A"), atTick: 110);

        Assert.Equal(1, r.Stats[K("A")].Kills);
        Assert.Equal(1, r.Stats[K("C")].Deaths);
    }

    [Fact]
    public void Kill_attributes_raw_damage_and_decay_weighted_credit()
    {
        var r = Make4Player();
        r.OnSpawn(K("C"), atTick: 0);
        r.OnDamage(K("C"), K("A"), 600, WeaponKind.Bullet, 0, atTick: 100);
        r.OnDamage(K("C"), K("B"), 200, WeaponKind.Bullet, 0, atTick: 110);
        r.OnKill(K("C"), K("A"), atTick: 110);

        // KillDamage is the raw (non-decay-weighted) recovery-adjusted contribution.
        Assert.Equal(600.0, r.Stats[K("A")].KillDamage, precision: 6);
        Assert.Equal(200.0, r.Stats[K("B")].KillDamage, precision: 6);

        // KillCredit is the decay-weighted share normalized so the event distributes 1.0 total.
        // Decay half-life=200. At tick 110, A's damage at 100 is 10 ticks old → weight 0.5^(10/200).
        // B's at tick 110 → weight 1.0.
        double aWeighted = 600 * Math.Pow(0.5, 10.0 / 200);
        double bWeighted = 200.0;
        double total = aWeighted + bWeighted;
        Assert.Equal(aWeighted / total, r.Stats[K("A")].KillCredit, precision: 6);
        Assert.Equal(bWeighted / total, r.Stats[K("B")].KillCredit, precision: 6);
        Assert.Equal(1.0, r.Stats[K("A")].KillCredit + r.Stats[K("B")].KillCredit, precision: 6);
    }

    [Fact]
    public void Kill_grants_assist_to_non_killer_above_threshold()
    {
        // Threshold = 25% of 1000 = 250. B contributed 300 → assist. D contributed 100 → no assist.
        var r = Make4Player();
        r.OnSpawn(K("C"), atTick: 0);
        r.OnDamage(K("C"), K("B"), 300, WeaponKind.Bullet, 0, atTick: 100);
        r.OnDamage(K("C"), K("A"), 600, WeaponKind.Bullet, 0, atTick: 100);
        r.OnDamage(K("C"), K("D"), 100, WeaponKind.Bullet, 0, atTick: 100); // teammate of victim — never assists
        r.OnKill(K("C"), K("A"), atTick: 100);

        Assert.Equal(0, r.Stats[K("A")].Assists);  // killer doesn't get an assist
        Assert.Equal(1, r.Stats[K("B")].Assists);  // above threshold
        Assert.Equal(0, r.Stats[K("D")].Assists);  // would be teammate-of-victim — not eligible
    }

    [Fact]
    public void Teamkill_increments_killer_teamkill_counter_not_kill()
    {
        var r = Make4Player();
        r.OnSpawn(K("B"), atTick: 0);
        r.OnDamage(K("B"), K("A"), 800, WeaponKind.Bomb, 0, atTick: 100);
        r.OnKill(victim: K("B"), killer: K("A"), atTick: 100);
        Assert.Equal(0, r.Stats[K("A")].Kills);
        Assert.Equal(1, r.Stats[K("A")].Teamkills);
        Assert.Equal(1, r.Stats[K("B")].Deaths);
    }

    [Fact]
    public void Self_kill_no_killer_attribution_no_kill_credit()
    {
        var r = Make4Player();
        r.OnSpawn(K("A"), atTick: 0);
        r.OnDamage(K("A"), K("A"), 800, WeaponKind.Bomb, 0, atTick: 100); // self-splash
        r.OnKill(victim: K("A"), killer: K("A"), atTick: 100);
        Assert.Equal(0, r.Stats[K("A")].Kills);
        Assert.Equal(1, r.Stats[K("A")].Deaths);
        Assert.Equal(0.0, r.Stats[K("A")].KillDamage);
    }

    [Fact]
    public void Knockout_flag_increments_knockout_counter()
    {
        var r = Make4Player();
        r.OnSpawn(K("C"), atTick: 0);
        r.OnDamage(K("C"), K("A"), 800, WeaponKind.Bullet, 0, atTick: 100);
        r.OnKill(K("C"), K("A"), atTick: 100, isKnockout: true);
        Assert.Equal(1, r.Stats[K("C")].Knockouts);
    }

    [Fact]
    public void Kill_closes_victim_life_with_knockout_by()
    {
        var r = Make4Player();
        r.OnSpawn(K("C"), atTick: 0);
        r.OnKill(K("C"), K("A"), atTick: 100);
        var life = r.Stats[K("C")].Lives.Single();
        Assert.True(life.IsClosed);
        Assert.Equal(LifeEndReason.KilledByEnemy, life.EndReason);
        Assert.Equal(K("A"), life.KnockoutBy);
    }

    [Fact]
    public void Kill_clears_victim_recovery_state()
    {
        var r = Make4Player();
        r.OnSpawn(K("C"), atTick: 0);
        r.OnDamage(K("C"), K("A"), 800, WeaponKind.Bullet, 0, atTick: 100);
        r.OnKill(K("C"), K("A"), atTick: 100);
        Assert.Equal(0, r.Stats[K("C")].Recovery.Count);
    }

    [Fact]
    public void Friendly_fire_damage_still_counts_in_kill_attribution_denominator()
    {
        // Per agreed semantics: teammate friendly fire shows up in killDamage attribution.
        var r = Make4Player();
        r.OnSpawn(K("C"), atTick: 0);
        r.OnDamage(K("C"), K("A"), 400, WeaponKind.Bullet, 0, atTick: 100);
        r.OnDamage(K("C"), K("D"), 200, WeaponKind.Bullet, 0, atTick: 100); // D is C's teammate
        r.OnKill(K("C"), K("A"), atTick: 100);
        // D still gets killDamage credit for their friendly fire on C. Only assists are gated.
        Assert.Equal(200.0, r.Stats[K("D")].KillDamage, precision: 6);
        Assert.Equal(0, r.Stats[K("D")].Assists);
    }

    // --- forced repel ---

    [Fact]
    public void Repel_attributes_forced_repel_damage_to_attackers_and_does_not_clear_recovery()
    {
        var r = Make4Player();
        r.OnDamage(K("C"), K("A"), 300, WeaponKind.Bullet, 0, atTick: 100);
        r.OnItemUsed(K("C"), ItemKind.Repel, atTick: 100);

        Assert.Equal(300.0, r.Stats[K("A")].ForcedRepelDamage, precision: 6);
        Assert.Equal(1.0, r.Stats[K("A")].ForcedRepelCredit, precision: 6);
        Assert.Equal(1, r.Stats[K("C")].ItemUses[ItemKind.Repel]);
        Assert.Equal(1, r.Stats[K("C")].Recovery.Count);
    }

    [Fact]
    public void Repel_credit_splits_one_unit_across_contributors_by_share()
    {
        // Two attackers contribute equally to the recovery state at repel time --
        // each receives 0.5 FR credit (total 1.0 for the event). Damage credit is full per share.
        var r = Make4Player();
        r.OnDamage(K("C"), K("A"), 200, WeaponKind.Bullet, 0, atTick: 100);
        r.OnDamage(K("C"), K("B"), 200, WeaponKind.Bullet, 0, atTick: 100);
        r.OnItemUsed(K("C"), ItemKind.Repel, atTick: 100);

        Assert.Equal(0.5, r.Stats[K("A")].ForcedRepelCredit, precision: 6);
        Assert.Equal(0.5, r.Stats[K("B")].ForcedRepelCredit, precision: 6);
        Assert.Equal(200.0, r.Stats[K("A")].ForcedRepelDamage, precision: 6);
        Assert.Equal(200.0, r.Stats[K("B")].ForcedRepelDamage, precision: 6);
    }

    [Fact]
    public void Repel_damage_is_raw_credit_is_decay_weighted()
    {
        // A hit much earlier than B; at repel time A's contribution decays toward 0 weight,
        // but ForcedRepelDamage is the RAW (non-weighted) recovery-adjusted amount, so A still
        // gets the full 200. ForcedRepelCredit normalizes the decay-weighted shares to 1.0.
        var r = Make4Player();
        r.OnDamage(K("C"), K("A"), 200, WeaponKind.Bullet, 0, atTick: 100);
        r.OnDamage(K("C"), K("B"), 200, WeaponKind.Bullet, 0, atTick: 110);
        r.OnItemUsed(K("C"), ItemKind.Repel, atTick: 110);

        Assert.Equal(200.0, r.Stats[K("A")].ForcedRepelDamage, precision: 6);
        Assert.Equal(200.0, r.Stats[K("B")].ForcedRepelDamage, precision: 6);

        double aWeighted = 200 * Math.Pow(0.5, 10.0 / 200);
        double bWeighted = 200.0;
        double total = aWeighted + bWeighted;
        Assert.Equal(aWeighted / total, r.Stats[K("A")].ForcedRepelCredit, precision: 6);
        Assert.Equal(bWeighted / total, r.Stats[K("B")].ForcedRepelCredit, precision: 6);
        Assert.Equal(1.0, r.Stats[K("A")].ForcedRepelCredit + r.Stats[K("B")].ForcedRepelCredit, precision: 6);
    }

    [Fact]
    public void Preemptive_repel_with_no_recent_damage_credits_no_one()
    {
        var r = Make4Player();
        r.OnItemUsed(K("C"), ItemKind.Repel, atTick: 100);
        Assert.Equal(0.0, r.Stats[K("A")].ForcedRepelDamage);
        Assert.Equal(0.0, r.Stats[K("A")].ForcedRepelCredit);
    }

    [Fact]
    public void Non_repel_item_use_only_bumps_counter()
    {
        var r = Make4Player();
        r.OnDamage(K("C"), K("A"), 300, WeaponKind.Bullet, 0, atTick: 100);
        r.OnItemUsed(K("C"), ItemKind.Burst, atTick: 100);
        Assert.Equal(0.0, r.Stats[K("A")].ForcedRepelDamage);
        Assert.Equal(1, r.Stats[K("C")].ItemUses[ItemKind.Burst]);
    }

    // --- ship change ---

    [Fact]
    public void Ship_change_applies_old_recharge_then_swaps_in_new()
    {
        var r = new StatsRecorder(new DamageDecay());
        var energy = SimpleEnergy();
        r.RegisterPlayer(K("A"), 0, 1000, rechargeRate: 1.0, energy, atTick: 0);
        r.OnDamage(K("A"), K("B"), 100, WeaponKind.Bullet, 0, atTick: 0);
        // Won't have B... the recorder skips silently. We just want to test the recovery state.
        // Re-do with B registered.

        r = new StatsRecorder(new DamageDecay());
        r.RegisterPlayer(K("A"), 0, 1000, 1.0, energy, 0);
        r.RegisterPlayer(K("B"), 1, 1000, 0.0, energy, 0);

        r.OnDamage(K("A"), K("B"), 100, WeaponKind.Bullet, 0, atTick: 0);
        r.OnShipChange(K("A"), newMaxEnergy: 2000, newRechargeRate: 2.0, energy, atTick: 50);
        // 50 ticks @ 1.0 → 50 recovered; 50 remains.
        var snap = r.Stats[K("A")].Recovery.Snapshot(currentTick: 50);
        Assert.Single(snap);
        Assert.Equal(50.0, snap[0].Amount, precision: 6);
    }

    // --- match flow ---

    [Fact]
    public void Match_ended_closes_open_lives_with_match_ended_reason()
    {
        var r = Make4Player();
        r.OnSpawn(K("A"), 0);
        r.OnSpawn(K("B"), 0);
        r.OnMatchEnded(atTick: 1000);
        Assert.Equal(LifeEndReason.MatchEnded, r.Stats[K("A")].Lives.Single().EndReason);
        Assert.Equal(LifeEndReason.MatchEnded, r.Stats[K("B")].Lives.Single().EndReason);
    }

    [Fact]
    public void Leave_match_closes_current_life_with_left_match()
    {
        var r = Make4Player();
        r.OnSpawn(K("A"), 0);
        r.OnLeaveMatch(K("A"), atTick: 500);
        var life = r.Stats[K("A")].Lives.Single();
        Assert.Equal(LifeEndReason.LeftMatch, life.EndReason);
        Assert.Null(life.KnockoutBy);
    }

    [Fact]
    public void Events_for_unregistered_players_are_silently_ignored()
    {
        var r = Make4Player();
        r.OnDamage(K("ZZZ"), K("A"), 100, WeaponKind.Bullet, 0, 100);
        r.OnKill(K("ZZZ"), K("A"), 100);
        r.OnSpawn(K("ZZZ"), 100);
        r.OnLeaveMatch(K("ZZZ"), 100);
        // Just ensure no exceptions and registered players unaffected.
        Assert.Equal(0, r.Stats[K("A")].Kills);
    }

    [Fact]
    public void OnDistanceSample_appends_in_order_and_clamps_negative_to_zero()
    {
        var r = Make4Player();
        r.OnDistanceSample(K("A"), 100, 256.5f);
        r.OnDistanceSample(K("A"), 120, 17.0f);
        r.OnDistanceSample(K("A"), 140, -3.0f);   // clamps to 0
        r.OnDistanceSample(K("ZZZ"), 100, 50.0f); // unregistered: silently ignored

        var samples = r.Stats[K("A")].DistanceSamples;
        Assert.Equal(3, samples.Count);
        Assert.Equal(100u, samples[0].Tick);
        Assert.Equal(256.5f, samples[0].Distance);
        Assert.Equal(17.0f, samples[1].Distance);
        Assert.Equal(0f, samples[2].Distance);
    }

    [Fact]
    public void SetWastedItem_records_and_clears()
    {
        var r = Make4Player();
        r.SetWastedItem(K("A"), ItemKind.Repel, 2);
        r.SetWastedItem(K("A"), ItemKind.Burst, 1);
        Assert.Equal(2, r.Stats[K("A")].WastedItems[ItemKind.Repel]);
        Assert.Equal(1, r.Stats[K("A")].WastedItems[ItemKind.Burst]);

        // Setting to 0 removes the entry rather than recording a zero.
        r.SetWastedItem(K("A"), ItemKind.Repel, 0);
        Assert.False(r.Stats[K("A")].WastedItems.ContainsKey(ItemKind.Repel));
        Assert.True(r.Stats[K("A")].WastedItems.ContainsKey(ItemKind.Burst));
    }

    private static IReadOnlyDictionary<ItemKind, int> Loadout(int repels, int bursts, int thors = 0) =>
        new Dictionary<ItemKind, int>
        {
            [ItemKind.Repel] = repels,
            [ItemKind.Burst] = bursts,
            [ItemKind.Thor] = thors,
        };

    /// <summary>
    /// Convenience wrapper for OnExtraPositionData: positional args are tedious for tests that
    /// only care about a couple of items, so this lets a test name only the columns it touches
    /// and leave the rest at zero. Mirrors the signature of the wire-authoritative writer.
    /// </summary>
    private static void Wire(
        StatsRecorder r, PlayerKey player,
        int bursts = 0, int repels = 0, int thors = 0,
        int bricks = 0, int decoys = 0, int rockets = 0, int portals = 0)
        => r.OnExtraPositionData(player, bursts, repels, thors, bricks, decoys, rockets, portals);

    [Fact]
    public void OnItemUsed_records_use_count_without_touching_inventory()
    {
        // Inventory is wire-authoritative now (driven by OnExtraPositionData). OnItemUsed only
        // bumps the per-key-press ItemUses counter and -- for repels -- triggers the
        // forced-repel attribution; it must not mutate Inventory, otherwise a self-reported
        // use racing the next position packet could double-count against the wire-reported state.
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), 0, Loadout(repels: 3, bursts: 1));
        r.OnSpawn(K("A"), 10);

        r.OnItemUsed(K("A"), ItemKind.Repel, 20);
        r.OnItemUsed(K("A"), ItemKind.Repel, 25);
        r.OnItemUsed(K("A"), ItemKind.Burst, 30);
        r.OnItemUsed(K("A"), ItemKind.Burst, 35);

        // ItemUses tracks the press count regardless of whether any item was actually held.
        Assert.Equal(2, r.Stats[K("A")].ItemUses[ItemKind.Repel]);
        Assert.Equal(2, r.Stats[K("A")].ItemUses[ItemKind.Burst]);

        // Inventory is unchanged from the spawn-time loadout: OnItemUsed never writes to it.
        Assert.Equal(3, r.Stats[K("A")].Inventory[ItemKind.Repel]);
        Assert.Equal(1, r.Stats[K("A")].Inventory[ItemKind.Burst]);
    }

    [Fact]
    public void OnExtraPositionData_mirrors_wire_counts_verbatim()
    {
        // The recorder no longer clamps or validates inventory counts -- Continuum reports the
        // post-cap, post-pickup, post-use number on the wire and we mirror it. Negative or zero
        // clears the entry (so a player who fires their last brick and the wire reports 0
        // doesn't keep a stale 1 in Inventory).
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), 0, Loadout(repels: 1, bursts: 0));

        // Wire reports counts above the initial loadout (greens picked up): mirrored as-is.
        r.OnExtraPositionData(K("A"), bursts: 4, repels: 5, thors: 2, bricks: 3, decoys: 1, rockets: 7, portals: 1);
        var inv = r.Stats[K("A")].Inventory;
        Assert.Equal(4, inv[ItemKind.Burst]);
        Assert.Equal(5, inv[ItemKind.Repel]);
        Assert.Equal(2, inv[ItemKind.Thor]);
        Assert.Equal(3, inv[ItemKind.Brick]);
        Assert.Equal(1, inv[ItemKind.Decoy]);
        Assert.Equal(7, inv[ItemKind.Rocket]);
        Assert.Equal(1, inv[ItemKind.Portal]);

        // Subsequent packet reports a zero column: the entry is removed, not stored as 0.
        r.OnExtraPositionData(K("A"), bursts: 4, repels: 0, thors: 2, bricks: 3, decoys: 1, rockets: 7, portals: 1);
        Assert.False(r.Stats[K("A")].Inventory.ContainsKey(ItemKind.Repel));
        Assert.Equal(4, r.Stats[K("A")].Inventory[ItemKind.Burst]);
    }

    [Fact]
    public void OnSpawn_resets_inventory_to_initial_loadout()
    {
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), 0, Loadout(repels: 3, bursts: 0));
        r.OnSpawn(K("A"), 10);

        // Mid-life the wire reports the player has used two repels.
        Wire(r, K("A"), repels: 1);
        Assert.Equal(1, r.Stats[K("A")].Inventory[ItemKind.Repel]);

        // Re-spawn after a death: inventory back to 3 (fresh life). The next position packet
        // from the new life will overwrite this with the wire count, but until then a reader
        // (e.g. statbox refresh on spawn) sees the loadout instead of stale end-of-prior-life.
        r.OnSpawn(K("A"), 100);
        Assert.Equal(3, r.Stats[K("A")].Inventory[ItemKind.Repel]);
    }

    [Fact]
    public void Knockout_snapshots_remaining_inventory_as_wasted()
    {
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        var energy = SimpleEnergy();
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, energy, 0, Loadout(repels: 3, bursts: 1, thors: 1));
        r.RegisterPlayer(K("B"), 1, 1000, 0.0, energy, 0);
        r.OnSpawn(K("A"), 10);
        // Wire reports A has used 1 of 3 repels (post-position-packet inventory).
        Wire(r, K("A"), repels: 2, bursts: 1, thors: 1);
        r.OnSpawn(K("B"), 10);

        // Final death (lives==0 -> isKnockout=true).
        r.OnKill(victim: K("A"), killer: K("B"), atTick: 50, isKnockout: true);

        var wasted = r.Stats[K("A")].WastedItems;
        Assert.Equal(2, wasted[ItemKind.Repel]);
        Assert.Equal(1, wasted[ItemKind.Burst]);
        Assert.Equal(1, wasted[ItemKind.Thor]);
    }

    [Fact]
    public void Non_knockout_death_accumulates_lifes_leftover_inventory()
    {
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        var energy = SimpleEnergy();
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, energy, 0, Loadout(repels: 3, bursts: 1));
        r.RegisterPlayer(K("B"), 1, 1000, 0.0, energy, 0);
        r.OnSpawn(K("A"), 10);
        Wire(r, K("A"), repels: 2, bursts: 1);

        r.OnKill(victim: K("A"), killer: K("B"), atTick: 50, isKnockout: false);

        // A had 2 repels + 1 burst still in inventory at the (non-knockout) close.
        var wasted = r.Stats[K("A")].WastedItems;
        Assert.Equal(2, wasted[ItemKind.Repel]);
        Assert.Equal(1, wasted[ItemKind.Burst]);
    }

    [Fact]
    public void Wasted_items_accumulate_across_multiple_lives()
    {
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        var energy = SimpleEnergy();
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, energy, 0, Loadout(repels: 3, bursts: 1));
        r.RegisterPlayer(K("B"), 1, 1000, 0.0, energy, 0);
        r.OnSpawn(K("B"), 5);

        // Life 1: A used 1 repel, dies non-knockout with 2 repels + 1 burst left on the wire.
        r.OnSpawn(K("A"), 10);
        Wire(r, K("A"), repels: 2, bursts: 1);
        r.OnKill(victim: K("A"), killer: K("B"), atTick: 30, isKnockout: false);

        // Life 2: A spawns fresh (back to 3 repels + 1 burst), uses 1 burst, dies knockout
        // with 3 repels + 0 bursts on the wire.
        r.OnSpawn(K("A"), 50);
        Wire(r, K("A"), repels: 3, bursts: 0);
        r.OnKill(victim: K("A"), killer: K("B"), atTick: 70, isKnockout: true);

        // Total wasted across both lives: 2 + 3 = 5 repels, 1 + 0 = 1 burst.
        var wasted = r.Stats[K("A")].WastedItems;
        Assert.Equal(5, wasted[ItemKind.Repel]);
        Assert.Equal(1, wasted[ItemKind.Burst]);
    }

    [Fact]
    public void OnMatchEnded_does_not_snapshot_survivors_final_life_inventory()
    {
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        var energy = SimpleEnergy();
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, energy, 0, Loadout(repels: 3, bursts: 0));
        r.RegisterPlayer(K("B"), 1, 1000, 0.0, energy, 0, Loadout(repels: 2, bursts: 1));
        r.OnSpawn(K("A"), 10);
        r.OnSpawn(K("B"), 10);

        // A is knocked out with 3 repels still held -- accumulated into wasted at the elimination.
        r.OnKill(victim: K("A"), killer: K("B"), atTick: 30, isKnockout: true);
        Assert.Equal(3, r.Stats[K("A")].WastedItems[ItemKind.Repel]);

        // B uses 2 repels and survives the match holding 1 burst.
        r.OnItemUsed(K("B"), ItemKind.Repel, 50);
        r.OnItemUsed(K("B"), ItemKind.Repel, 60);
        r.OnMatchEnded(atTick: 100);

        // Wasted-items count only the items unused at time of death/elimination -- a player who
        // survives to the buzzer takes their leftover inventory home and does not accrue wasted
        // counts for it.
        Assert.False(r.Stats[K("B")].WastedItems.ContainsKey(ItemKind.Repel));
        Assert.False(r.Stats[K("B")].WastedItems.ContainsKey(ItemKind.Burst));

        // A's tally is unchanged at match-end.
        Assert.Equal(3, r.Stats[K("A")].WastedItems[ItemKind.Repel]);
    }

    [Fact]
    public void Wire_reported_pickups_increase_wasted_when_unused_at_life_close()
    {
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        var energy = SimpleEnergy();
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, energy, 0, Loadout(repels: 1, bursts: 0));
        r.RegisterPlayer(K("B"), 1, 1000, 0.0, energy, 0);
        r.OnSpawn(K("A"), 10);
        r.OnSpawn(K("B"), 10);

        // A picks up two repels and a burst mid-life: the wire reports the post-pickup
        // counts on the next position packet. OnPrizePickup itself is a no-op now.
        r.OnPrizePickup(K("A"), ItemKind.Repel);
        Wire(r, K("A"), repels: 2);
        r.OnPrizePickup(K("A"), ItemKind.Repel);
        r.OnPrizePickup(K("A"), ItemKind.Burst);
        Wire(r, K("A"), repels: 3, bursts: 1);

        // Knockout with 3 repels + 1 burst still on the wire -> all go to wasted.
        r.OnKill(victim: K("A"), killer: K("B"), atTick: 50, isKnockout: true);
        var wasted = r.Stats[K("A")].WastedItems;
        Assert.Equal(3, wasted[ItemKind.Repel]);
        Assert.Equal(1, wasted[ItemKind.Burst]);
    }

    [Fact]
    public void OnPrizePickup_is_a_no_op_under_wire_authoritative_inventory()
    {
        // Continuum reports the post-pickup count on the next position packet, so the recorder
        // no longer reconstructs from pickup events. Calling OnPrizePickup must not change
        // Inventory; until a wire update arrives the count stays at the spawn-time loadout.
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), 0, Loadout(repels: 1, bursts: 0));
        r.OnSpawn(K("A"), 10);

        for (int i = 0; i < 5; i++) r.OnPrizePickup(K("A"), ItemKind.Repel);
        for (int i = 0; i < 5; i++) r.OnPrizePickup(K("A"), ItemKind.Burst);

        Assert.Equal(1, r.Stats[K("A")].Inventory[ItemKind.Repel]);
        Assert.False(r.Stats[K("A")].Inventory.ContainsKey(ItemKind.Burst));
    }

    // --- active ticks via position packets ---

    [Fact]
    public void OnPositionPacket_first_packet_seeds_anchor_without_crediting()
    {
        var r = Make4Player();
        r.OnPositionPacket(K("A"), atTick: 100, energy: 0);
        Assert.Equal(0u, r.Stats[K("A")].ActiveTicks);
    }

    [Fact]
    public void OnPositionPacket_credits_inter_packet_delta()
    {
        var r = Make4Player();
        r.OnPositionPacket(K("A"), atTick: 100, energy: 0);
        r.OnPositionPacket(K("A"), atTick: 110, energy: 0);
        r.OnPositionPacket(K("A"), atTick: 130, energy: 0);
        Assert.Equal(30u, r.Stats[K("A")].ActiveTicks);
    }

    [Fact]
    public void OnPositionPacket_caps_oversized_gap_at_max_active_delta()
    {
        var r = Make4Player();
        r.OnPositionPacket(K("A"), atTick: 100, energy: 0);
        // Gap of 1000 ticks (10 s) should cap at MaxActiveDeltaTicks (300 ticks / 3 s).
        r.OnPositionPacket(K("A"), atTick: 1100, energy: 0);
        Assert.Equal(StatsRecorder.MaxActiveDeltaTicks, r.Stats[K("A")].ActiveTicks);
    }

    [Fact]
    public void OnPositionPacket_resets_anchor_on_spawn()
    {
        var r = Make4Player();
        r.OnPositionPacket(K("A"), atTick: 100, energy: 0);
        r.OnSpawn(K("A"), atTick: 105);
        // The post-spawn packet should seed a new anchor, not credit the 105->110 since-spawn gap
        // through the pre-spawn anchor.
        r.OnPositionPacket(K("A"), atTick: 110, energy: 0);
        r.OnPositionPacket(K("A"), atTick: 120, energy: 0);
        Assert.Equal(10u, r.Stats[K("A")].ActiveTicks);
    }

    [Fact]
    public void OnPositionPacket_resets_anchor_on_leave_match()
    {
        var r = Make4Player();
        r.OnPositionPacket(K("A"), atTick: 100, energy: 0);
        r.OnLeaveMatch(K("A"), atTick: 200);
        // Re-registration would normally happen for a sub; for this test we just verify the
        // anchor was cleared so a stale delta isn't credited if a packet somehow arrives.
        r.OnPositionPacket(K("A"), atTick: 5000, energy: 0);
        Assert.Equal(0u, r.Stats[K("A")].ActiveTicks);
    }

    // --- duplicate-death suppression ---

    [Fact]
    public void OnKill_duplicate_without_intervening_alive_packet_is_dropped()
    {
        var r = Make4Player();
        r.OnKill(victim: K("A"), killer: K("C"), atTick: 100);
        // The buggy client sends a second death packet a few ticks later.
        r.OnKill(victim: K("A"), killer: K("C"), atTick: 103);
        Assert.Equal(1, r.Stats[K("A")].Deaths);
        Assert.Equal(1, r.Stats[K("C")].Kills);
    }

    [Fact]
    public void OnKill_after_full_energy_position_packet_counts()
    {
        var r = Make4Player();
        r.OnKill(victim: K("A"), killer: K("C"), atTick: 100);
        // Without an intervening OnSpawn, only the >75%-energy position packet can clear
        // the gate. Energy 800 / max 1000 = 80% qualifies.
        r.OnPositionPacket(K("A"), atTick: 200, energy: 800);
        r.OnKill(victim: K("A"), killer: K("C"), atTick: 300);
        Assert.Equal(2, r.Stats[K("A")].Deaths);
        Assert.Equal(2, r.Stats[K("C")].Kills);
    }

    [Fact]
    public void OnKill_after_spawn_counts_even_without_position_packet()
    {
        var r = Make4Player();
        r.OnKill(victim: K("A"), killer: K("C"), atTick: 100);
        r.OnSpawn(K("A"), atTick: 200);
        r.OnKill(victim: K("A"), killer: K("C"), atTick: 300);
        Assert.Equal(2, r.Stats[K("A")].Deaths);
    }

    [Fact]
    public void OnKill_low_energy_position_packet_does_not_clear_gate()
    {
        var r = Make4Player();
        r.OnKill(victim: K("A"), killer: K("C"), atTick: 100);
        // 75% of 1000 max is 750; energy at exactly 75% is not strictly above the threshold.
        r.OnPositionPacket(K("A"), atTick: 150, energy: 750);
        // Even a respawn packet doesn't help if it's still at 50%.
        r.OnPositionPacket(K("A"), atTick: 160, energy: 500);
        r.OnKill(victim: K("A"), killer: K("C"), atTick: 170);
        Assert.Equal(1, r.Stats[K("A")].Deaths);
    }

    [Fact]
    public void OnKill_position_packet_predating_death_tick_does_not_clear_gate()
    {
        var r = Make4Player();
        r.OnKill(victim: K("A"), killer: K("C"), atTick: 100);
        // Out-of-order arrival: the packet is processed after the death but its client tick
        // is older than the death's tick. It must not retroactively prove the player came
        // back alive.
        r.OnPositionPacket(K("A"), atTick: 90, energy: 1000);
        r.OnKill(victim: K("A"), killer: K("C"), atTick: 105);
        Assert.Equal(1, r.Stats[K("A")].Deaths);
    }

    // --- last-leave inventory snapshot (consumed by ?return restore path) ---

    [Fact]
    public void CaptureLastLeaveInventory_freezes_current_inventory()
    {
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), 0, Loadout(repels: 3, bursts: 1));
        r.OnSpawn(K("A"), 10);

        // Wire reports mid-life: 1 of 3 repels and 0 of 1 bursts left.
        Wire(r, K("A"), repels: 1, bursts: 0);

        r.CaptureLastLeaveInventory(K("A"));
        Assert.True(r.TryGetLastLeaveInventory(K("A"), out var snap));
        Assert.NotNull(snap);
        Assert.Equal(1, snap![ItemKind.Repel]);
        Assert.False(snap.ContainsKey(ItemKind.Burst));
    }

    [Fact]
    public void CaptureLastLeaveInventory_decouples_from_live_inventory()
    {
        // The snapshot must be a deep copy: subsequent OnExtraPositionData updates to the live
        // inventory must not bleed into the captured snapshot. Otherwise a stray spec-mode
        // packet (or a stale tick) would silently mutate the value the orchestrator is about
        // to consume.
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), 0, Loadout(repels: 3, bursts: 1));
        r.OnSpawn(K("A"), 10);
        Wire(r, K("A"), repels: 2, bursts: 1);

        r.CaptureLastLeaveInventory(K("A"));

        // Live inventory churns after the capture.
        Wire(r, K("A"), repels: 0, bursts: 0);

        Assert.True(r.TryGetLastLeaveInventory(K("A"), out var snap));
        Assert.Equal(2, snap![ItemKind.Repel]);
        Assert.Equal(1, snap[ItemKind.Burst]);
    }

    [Fact]
    public void OnSpawn_clears_last_leave_inventory()
    {
        // A real respawn supersedes any saved return-snapshot: the orchestrator should have
        // already consumed it before SpawnCallback fires, and a stale snapshot left behind would
        // be the wrong loadout to restore on a *future* spec-and-return cycle.
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), 0, Loadout(repels: 3, bursts: 1));
        r.OnSpawn(K("A"), 10);
        Wire(r, K("A"), repels: 2, bursts: 1);
        r.CaptureLastLeaveInventory(K("A"));
        Assert.True(r.TryGetLastLeaveInventory(K("A"), out _));

        r.OnSpawn(K("A"), 100);
        Assert.False(r.TryGetLastLeaveInventory(K("A"), out var snap));
        Assert.Null(snap);
    }

    [Fact]
    public void OnLeaveMatch_clears_last_leave_inventory()
    {
        // A true exit (sub-out, post-grace release) means the player isn't coming back via
        // ?return; the saved snapshot must be evicted so a re-registered player on the same key
        // doesn't inherit it.
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), 0, Loadout(repels: 3, bursts: 0));
        r.OnSpawn(K("A"), 10);
        Wire(r, K("A"), repels: 2);
        r.CaptureLastLeaveInventory(K("A"));

        r.OnLeaveMatch(K("A"), atTick: 50);
        Assert.False(r.TryGetLastLeaveInventory(K("A"), out _));
    }

    [Fact]
    public void TryGetLastLeaveInventory_returns_false_for_unknown_player()
    {
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        Assert.False(r.TryGetLastLeaveInventory(K("nobody"), out var snap));
        Assert.Null(snap);
    }

    [Fact]
    public void CaptureLastLeaveInventory_for_unregistered_player_is_a_noop()
    {
        // Capture against a key the recorder doesn't know about must not throw, must not create
        // a phantom snapshot. Defensive: the orchestrator may racily call this for a player who
        // was just released from the match-roster.
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.CaptureLastLeaveInventory(K("ghost"));
        Assert.False(r.TryGetLastLeaveInventory(K("ghost"), out _));
    }

    [Fact]
    public void TryGetInitialInventory_returns_registered_loadout()
    {
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), 0, Loadout(repels: 3, bursts: 1));
        Assert.True(r.TryGetInitialInventory(K("A"), out var initial));
        Assert.Equal(3, initial![ItemKind.Repel]);
        Assert.Equal(1, initial[ItemKind.Burst]);
    }

    [Fact]
    public void TryGetInitialInventory_reflects_post_ship_change_loadout()
    {
        // OnShipChange may swap the per-ship initial loadout (different ship has different
        // InitialBurst / InitialRepel). The ?return restore path needs the *current* ship's
        // initial counts so the deduction math (initial - saved) lands on the right baseline.
        var r = new StatsRecorder(new DamageDecay(halfLifeTicks: 200));
        r.RegisterPlayer(K("A"), 0, 1000, 0.0, SimpleEnergy(), 0, Loadout(repels: 3, bursts: 1));
        r.OnShipChange(K("A"), newMaxEnergy: 1200, newRechargeRate: 1.0, SimpleEnergy(), atTick: 50,
            newInitialInventory: Loadout(repels: 1, bursts: 4));
        Assert.True(r.TryGetInitialInventory(K("A"), out var initial));
        Assert.Equal(1, initial![ItemKind.Repel]);
        Assert.Equal(4, initial[ItemKind.Burst]);
    }
}
