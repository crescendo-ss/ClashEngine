using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Orchestrates per-match stat accounting. Receives event-level inputs from the adapter
/// (damage, kill, weapon-fire, item-use, spawn, ship-change, leave) and dispatches them to
/// the right <see cref="PlayerStats"/>, including the cross-player attribution work on kill
/// and forced-repel.
/// </summary>
/// <remarks>
/// Single-threaded. One <see cref="StatsRecorder"/> per match. The adapter is expected to
/// register every participant (including subs) via <see cref="RegisterPlayer"/> before
/// dispatching their events.
/// </remarks>
public sealed class StatsRecorder
{
    private readonly Dictionary<PlayerKey, PlayerStats> _stats = new();
    private readonly Dictionary<PlayerKey, PlayerProfile> _profiles = new();
    private readonly Dictionary<PlayerKey, uint> _lastPositionTick = new();
    private readonly Dictionary<PlayerKey, uint> _pendingDeathTick = new();
    private readonly DamageDecay _decay;
    private readonly double _assistThresholdFraction;

    public const double DefaultAssistThresholdFraction = 0.25;

    /// <summary>
    /// Cap on the inter-packet delta credited to <see cref="PlayerStats.ActiveTicks"/>. Position
    /// packets normally arrive every 10-20 ticks (100-200 ms); a large gap usually indicates a
    /// stutter, spec window, or arena hop and should not roll up into a player's "playing time".
    /// 300 ticks = 3 seconds.
    /// </summary>
    public const uint MaxActiveDeltaTicks = 300;

    /// <summary>
    /// Energy threshold (as a fraction of the player's max) that a post-death position packet
    /// must exceed for us to consider the player "back alive" -- i.e. for the next death packet
    /// to count. The Continuum client occasionally double-sends a death packet, and this gate
    /// suppresses the second one. 75% is well above any realistic damaged-but-alive snapshot
    /// since fresh spawns come in at full energy.
    /// </summary>
    public const double AliveEnergyThresholdFraction = 0.75;

    public StatsRecorder(DamageDecay decay, double assistThresholdFraction = DefaultAssistThresholdFraction)
    {
        ArgumentNullException.ThrowIfNull(decay);
        if (assistThresholdFraction < 0 || assistThresholdFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(assistThresholdFraction), "Must be in [0, 1].");
        _decay = decay;
        _assistThresholdFraction = assistThresholdFraction;
    }

    public IReadOnlyDictionary<PlayerKey, PlayerStats> Stats => _stats;
    public DamageDecay Decay => _decay;
    public double AssistThresholdFraction => _assistThresholdFraction;

    /// <summary>
    /// Add a participant to the match. Must be called before any event for that player.
    /// </summary>
    /// <param name="maxInventory">Optional per-item cap mirroring the ship's <c>BurstMax</c> /
    /// <c>RepelMax</c> / etc. Continuum drops a green when the player is already at the cap, so
    /// <see cref="OnPrizePickup"/> consults this to avoid inflating the wasted-items tally with
    /// pickups the client never actually retained.</param>
    public void RegisterPlayer(
        PlayerKey player,
        int teamIndex,
        int maxEnergy,
        double rechargeRate,
        WeaponEnergyConfig energyConfig,
        uint atTick,
        IReadOnlyDictionary<ItemKind, int>? initialInventory = null,
        IReadOnlyDictionary<ItemKind, int>? maxInventory = null)
    {
        ArgumentNullException.ThrowIfNull(energyConfig);
        if (player.IsDefault) throw new ArgumentException("Player key must be non-default.", nameof(player));
        if (_stats.ContainsKey(player)) throw new InvalidOperationException($"Player {player} already registered.");
        if (maxEnergy <= 0) throw new ArgumentOutOfRangeException(nameof(maxEnergy), "Must be positive.");

        var stats = new PlayerStats(player, new DamageRecoveryState(rechargeRate, initialTick: atTick));
        // Pre-load inventory so a slot read between RegisterPlayer and the first OnSpawn doesn't
        // see a zeroed loadout (e.g. MatchLvz refreshing on match start before any spawn).
        stats.ResetInventory(initialInventory);
        // Open the first life anchored to the match-start tick so we have a life to attribute
        // damage / deaths to even if the host never emits an initial SpawnCallback (or if the
        // player is killed before the first spawn callback fires). OpenLife is idempotent, so
        // a real OnSpawn arriving later is a no-op against this initial life.
        stats.OpenLife(atTick);
        _stats[player] = stats;
        _profiles[player] = new PlayerProfile(teamIndex, maxEnergy, energyConfig, initialInventory, maxInventory);
    }

    /// <summary>
    /// Update a player's ship-derived parameters (recharge rate, max energy, weapon energy
    /// config). Recharge accumulated up to <paramref name="atTick"/> is applied at the old
    /// rate before the new one takes effect.
    /// </summary>
    public void OnShipChange(
        PlayerKey player,
        int newMaxEnergy,
        double newRechargeRate,
        WeaponEnergyConfig newEnergyConfig,
        uint atTick,
        IReadOnlyDictionary<ItemKind, int>? newInitialInventory = null,
        IReadOnlyDictionary<ItemKind, int>? newMaxInventory = null)
    {
        if (!_stats.TryGetValue(player, out var stats)) return;
        if (newMaxEnergy <= 0) throw new ArgumentOutOfRangeException(nameof(newMaxEnergy), "Must be positive.");
        ArgumentNullException.ThrowIfNull(newEnergyConfig);

        stats.Recovery.SetRechargeRate(newRechargeRate, atTick);
        var profile = _profiles[player];
        _profiles[player] = profile with
        {
            MaxEnergy = newMaxEnergy,
            EnergyConfig = newEnergyConfig,
            InitialInventory = newInitialInventory ?? profile.InitialInventory,
            MaxInventory = newMaxInventory ?? profile.MaxInventory,
        };
    }

    /// <summary>Player has spawned. Opens a new life and refreshes the held-item inventory to
    /// the ship's initial loadout (since each spawn comes with fresh items in Continuum).
    /// Idempotent if a life is already open.</summary>
    public void OnSpawn(PlayerKey player, uint atTick)
    {
        if (!_stats.TryGetValue(player, out var stats)) return;
        stats.OpenLife(atTick);
        if (_profiles.TryGetValue(player, out var profile))
            stats.ResetInventory(profile.InitialInventory);
        // Reset the position-tick anchor so the gap across the death-respawn cycle isn't
        // credited as active time on the next packet.
        _lastPositionTick.Remove(player);
        // The spawn event is server-authoritative proof the player is back alive, so
        // it also clears the duplicate-death gate. The position-packet branch in
        // OnPositionPacket is the wire-side fallback for cases where SpawnCallback
        // hasn't fired yet.
        _pendingDeathTick.Remove(player);
    }

    /// <summary>
    /// Called once per inbound position packet. Credits the elapsed-since-previous interval to
    /// <see cref="PlayerStats.ActiveTicks"/>, capped at <see cref="MaxActiveDeltaTicks"/> so a
    /// stutter or spec/arena hop doesn't inflate playing time. The first packet after spawn or
    /// match start seeds the anchor without crediting any time. A packet at <paramref name="atTick"/>
    /// later than the player's last recorded death tick and carrying energy above
    /// <see cref="AliveEnergyThresholdFraction"/> of the player's max also clears the
    /// duplicate-death gate set by <see cref="OnKill"/>.
    /// </summary>
    public void OnPositionPacket(PlayerKey player, uint atTick, int energy)
    {
        if (!_stats.TryGetValue(player, out var stats)) return;
        if (_lastPositionTick.TryGetValue(player, out var prev) && atTick > prev)
        {
            uint delta = atTick - prev;
            if (delta > MaxActiveDeltaTicks) delta = MaxActiveDeltaTicks;
            stats.AddActiveTicks(delta);
        }
        _lastPositionTick[player] = atTick;

        if (_pendingDeathTick.TryGetValue(player, out var deathTick)
            && atTick > deathTick
            && _profiles.TryGetValue(player, out var profile)
            && energy > profile.MaxEnergy * AliveEnergyThresholdFraction)
        {
            _pendingDeathTick.Remove(player);
        }
    }

    /// <summary>Append a distance-to-nearest-enemy sample. No-op if the player isn't registered.</summary>
    public void OnDistanceSample(PlayerKey player, uint atTick, float distancePixels)
    {
        if (!_stats.TryGetValue(player, out var stats)) return;
        stats.AppendDistanceSample(atTick, distancePixels);
    }

    /// <summary>Set a wasted-items count for the player. Pass 0 to clear.</summary>
    public void SetWastedItem(PlayerKey player, ItemKind item, int count)
    {
        if (!_stats.TryGetValue(player, out var stats)) return;
        stats.SetWastedItem(item, count);
    }

    /// <summary>
    /// A stockpilable item was picked up via green-prize. Now a no-op: inventory is wire-
    /// authoritative (driven by <see cref="OnExtraPositionData"/>), and Continuum reports the
    /// post-pickup count in the next position packet's <c>ExtraPositionData</c>. Kept on the
    /// public surface to avoid breaking adapter callers; can be removed once the SS-side
    /// listener stops invoking it.
    /// </summary>
    public void OnPrizePickup(PlayerKey player, ItemKind item)
    {
        // Intentionally empty. See remarks above.
    }

    /// <summary>
    /// Wire-authoritative inventory update from a position packet's <c>ExtraPositionData</c>.
    /// Continuum reports the player's actual current item counts in every extra-position packet,
    /// so we mirror those values directly into <see cref="PlayerStats.Inventory"/>. Replaces the
    /// reconstructive decrement-on-use / increment-on-pickup model that was vulnerable to
    /// duplicate-packet drift (repels) and missed entirely the items not delivered via the
    /// position-packet weapon field (rockets, bricks, portals).
    /// </summary>
    /// <remarks>
    /// Counts are <see cref="int"/> for caller convenience but originate as <see cref="byte"/>
    /// fields in <c>ExtraPositionData</c>. Negative or zero values clear the entry.
    /// </remarks>
    public void OnExtraPositionData(
        PlayerKey player,
        int bursts,
        int repels,
        int thors,
        int bricks,
        int decoys,
        int rockets,
        int portals)
    {
        if (!_stats.TryGetValue(player, out var stats)) return;
        stats.SetInventoryItem(ItemKind.Burst, bursts);
        stats.SetInventoryItem(ItemKind.Repel, repels);
        stats.SetInventoryItem(ItemKind.Thor, thors);
        stats.SetInventoryItem(ItemKind.Brick, bricks);
        stats.SetInventoryItem(ItemKind.Decoy, decoys);
        stats.SetInventoryItem(ItemKind.Rocket, rockets);
        stats.SetInventoryItem(ItemKind.Portal, portals);
    }

    /// <summary>
    /// A damage packet from <paramref name="victim"/>'s client crediting damage to
    /// <paramref name="attacker"/>. Self-damage (<paramref name="attacker"/> == <paramref name="victim"/>)
    /// is recorded as <see cref="PlayerStats.SelfDamage"/> only -- it does not go on the recovery state.
    /// </summary>
    public void OnDamage(
        PlayerKey victim,
        PlayerKey attacker,
        short amount,
        WeaponKind weapon,
        uint empDurationTicks,
        uint atTick)
    {
        if (amount <= 0) return;
        if (!_stats.TryGetValue(victim, out var victimStats)) return;

        if (attacker == victim)
        {
            victimStats.AddSelfDamage(amount);
            return;
        }

        if (!_stats.TryGetValue(attacker, out var attackerStats)) return;

        bool sameTeam = SameTeam(attacker, victim);

        if (sameTeam)
        {
            attackerStats.AddTeamDamageDealt(amount);
            victimStats.AddTeamDamageTaken(amount);
        }
        else
        {
            attackerStats.AddDamageDealt(amount);
            victimStats.AddDamageTaken(amount);
        }

        // Record on the victim's recovery state regardless of team -- friendly fire still
        // contributes to kill credit (per the agreed denominator), and an enemy repel triggered
        // off teammate-inflicted pressure is still attributable to that teammate.
        victimStats.Recovery.OnDamage(new DamageEntry(atTick, amount, weapon, empDurationTicks, attacker));

        if (weapon.IsTrackedForAccuracy())
            attackerStats.GetOrCreateWeapon(weapon).RecordHit();
    }

    /// <summary>
    /// A weapon-fire event derived from <paramref name="player"/>'s position packet. Computes
    /// the energy cost from the player's <see cref="WeaponEnergyConfig"/> and bumps the
    /// per-weapon shot/energy counters. Untracked kinds (shrapnel, burst pellets) are ignored.
    /// </summary>
    public void OnWeaponFired(
        PlayerKey player,
        WeaponKind weapon,
        int level,
        bool multifire,
        uint atTick)
    {
        if (!weapon.IsTrackedForAccuracy()) return;
        if (!_stats.TryGetValue(player, out var stats)) return;
        if (!_profiles.TryGetValue(player, out var profile)) return;

        int energy = profile.EnergyConfig.EnergyForShot(weapon, level, multifire);
        stats.GetOrCreateWeapon(weapon).RecordShot(energy);
    }

    /// <summary>
    /// Player <paramref name="victim"/> was killed by <paramref name="killer"/>. Snapshots the
    /// victim's recovery state, decay-weights, and partitions the result among all attackers
    /// (including teammates of the victim -- the "friendly fire still counts toward kill credit"
    /// rule). Assists go to non-killer attackers on the opposite team whose decay-weighted
    /// share met the assist threshold.
    /// </summary>
    public void OnKill(PlayerKey victim, PlayerKey killer, uint atTick, bool isKnockout = false)
    {
        if (!_stats.TryGetValue(victim, out var victimStats)) return;
        if (!_profiles.TryGetValue(victim, out var victimProfile)) return;

        // Drop duplicate death packets from the buggy client. The gate is cleared by a
        // post-death position packet showing the player back at >75% energy
        // (see OnPositionPacket); until that arrives, treat any further death as the
        // duplicate of the one we already recorded.
        if (_pendingDeathTick.ContainsKey(victim)) return;
        _pendingDeathTick[victim] = atTick;

        victimStats.RecordDeath();
        if (isKnockout) victimStats.RecordKnockout();

        // Every life-close accumulates leftover inventory into the wasted bucket. On a
        // non-knockout death the next OnSpawn refills inventory to the ship's initial loadout
        // before the new life starts, so each life's wastage is tallied independently. On the
        // final knockout there is no next spawn and the snapshot is the player's last contribution.
        victimStats.SnapshotInventoryAsWasted();

        // Self-kill (own splash, etc.): no kill credit, no attribution. Close life with
        // KilledByEnemy and no knockoutBy -- the game's kill packet says it but the credit's
        // empty.
        if (killer == victim)
        {
            victimStats.CloseLife(atTick, LifeEndReason.KilledByEnemy, knockoutBy: null);
            victimStats.Recovery.Clear();
            return;
        }

        bool sameTeam = SameTeam(killer, victim);
        if (_stats.TryGetValue(killer, out var killerStats))
        {
            if (sameTeam) killerStats.RecordTeamkill();
            else killerStats.RecordKill();
        }

        // Single pass producing per-attacker (raw, weighted) pairs. KillDamage takes the raw
        // recovery-adjusted amount; KillCredit takes the decay-weighted share normalized so the
        // event distributes a total of 1.0 credit across contributors. Assist eligibility uses
        // the decay-weighted share so it correctly devalues old hits.
        var (attribution, totalWeighted) = AttributeWithNormalization(victimStats, atTick);
        double assistThreshold = victimProfile.MaxEnergy * _assistThresholdFraction;

        foreach (var (attacker, share) in attribution)
        {
            if (!_stats.TryGetValue(attacker, out var attackerStats)) continue;
            attackerStats.AddKillDamage(share.Raw);
            if (totalWeighted > 0) attackerStats.AddKillCredit(share.Weighted / totalWeighted);

            if (attacker == killer) continue;             // killer gets the kill, not an assist
            if (SameTeam(attacker, victim)) continue;     // teammate of victim cannot "assist" the kill
            if (share.Weighted >= assistThreshold)
                attackerStats.RecordAssist();
        }

        victimStats.CloseLife(
            atTick,
            sameTeam ? LifeEndReason.KilledByTeammate : LifeEndReason.KilledByEnemy,
            knockoutBy: killer);
        victimStats.Recovery.Clear();
    }

    /// <summary>
    /// Player used an item. Bumps the per-item-press counter (used by the stats payload) and,
    /// for <see cref="ItemKind.Repel"/>, snapshots the user's recovery state and credits each
    /// recent attacker with decay-weighted forced-repel damage. The recovery state is <em>not</em>
    /// cleared by a repel -- the underlying damage is still unrecovered. Inventory tracking is
    /// wire-authoritative now (see <see cref="OnExtraPositionData"/>); this method no longer
    /// decrements the running inventory.
    /// </summary>
    public void OnItemUsed(PlayerKey player, ItemKind item, uint atTick)
    {
        if (!_stats.TryGetValue(player, out var stats)) return;
        stats.RecordItemUse(item);

        if (item != ItemKind.Repel) return;

        // Same shape as the kill path. ForcedRepelDamage takes the raw recovery-adjusted
        // amount; ForcedRepelCredit takes the decay-weighted share normalized so one repel
        // distributes 1.0 across contributors.
        var (attribution, totalWeighted) = AttributeWithNormalization(stats, atTick);

        foreach (var (attacker, share) in attribution)
        {
            if (!_stats.TryGetValue(attacker, out var attackerStats)) continue;
            attackerStats.AddForcedRepelDamage(share.Raw);
            if (totalWeighted > 0) attackerStats.AddForcedRepelCredit(share.Weighted / totalWeighted);
        }
    }

    /// <summary>
    /// One pass over <paramref name="victimStats"/>'s recent (unrecovered) damage that returns
    /// per-attacker (raw, weighted) pairs alongside the sum of weighted contributions -- the
    /// denominator the kill / forced-repel paths use to normalize each attacker's share into a
    /// fractional credit (0..1).
    /// </summary>
    private (Dictionary<PlayerKey, AttributedDamage> Attribution, double TotalWeighted)
        AttributeWithNormalization(PlayerStats victimStats, uint atTick)
    {
        var attribution = DamageAttribution.AttributeRawAndWeighted(
            victimStats.Recovery.Snapshot(atTick), atTick, _decay);
        double totalWeighted = 0;
        foreach (var (_, share) in attribution) totalWeighted += share.Weighted;
        return (attribution, totalWeighted);
    }

    /// <summary>Player left the match (disconnected, switched to spec, etc.).</summary>
    public void OnLeaveMatch(PlayerKey player, uint atTick)
    {
        if (!_stats.TryGetValue(player, out var stats)) return;
        stats.SnapshotInventoryAsWasted();
        stats.CloseLife(atTick, LifeEndReason.LeftMatch, knockoutBy: null);
        _lastPositionTick.Remove(player);
        _pendingDeathTick.Remove(player);
    }

    /// <summary>Match ended. Closes any open life on every registered player. Wasted-items are
    /// only tallied at death/elimination -- survivors hanging onto items at the buzzer don't
    /// have those items folded into their wasted-items count, since "wasted" is meant to
    /// quantify items the player took to the grave on a prior life, not the final-life
    /// leftover from a match they actually survived.</summary>
    public void OnMatchEnded(uint atTick)
    {
        foreach (var stats in _stats.Values)
            stats.CloseLife(atTick, LifeEndReason.MatchEnded, knockoutBy: null);
    }

    /// <summary>
    /// Snapshot of the victim's outstanding (unrecovered) damage at <paramref name="atTick"/>,
    /// partitioned by attacker, for use by an in-arena kill-feed line. Returns the killer's raw
    /// damage to the victim plus any opposite-team attackers as assisters (descending damage).
    /// Teammates of the victim and self-damage are filtered out. Caller must invoke this before
    /// <see cref="OnKill"/> for the same kill, since OnKill clears the recovery state.
    /// </summary>
    public KillFeedSnapshot? BuildKillFeed(PlayerKey victim, PlayerKey killer, uint atTick)
    {
        if (!_stats.TryGetValue(victim, out var victimStats)) return null;
        var attribution = DamageAttribution.AttributeRawAndWeighted(
            victimStats.Recovery.Snapshot(atTick), atTick, _decay);

        int killerDamage = 0;
        if (attribution.TryGetValue(killer, out var k))
            killerDamage = (int)Math.Round(k.Raw);

        var assisters = new List<KillFeedAssister>();
        foreach (var (atk, share) in attribution)
        {
            if (atk.Equals(killer)) continue;       // killer line, not an assist
            if (atk.Equals(victim)) continue;       // self-damage isn't an assist
            if (SameTeam(atk, victim)) continue;    // victim's teammates aren't assisters
            int dmg = (int)Math.Round(share.Raw);
            if (dmg <= 0) continue;
            assisters.Add(new KillFeedAssister(atk, dmg));
        }
        assisters.Sort((a, b) => b.Damage.CompareTo(a.Damage));
        return new KillFeedSnapshot(killerDamage, assisters);
    }

    private bool SameTeam(PlayerKey a, PlayerKey b)
    {
        if (!_profiles.TryGetValue(a, out var pa)) return false;
        if (!_profiles.TryGetValue(b, out var pb)) return false;
        return pa.TeamIndex == pb.TeamIndex;
    }

    private readonly record struct PlayerProfile(
        int TeamIndex,
        int MaxEnergy,
        WeaponEnergyConfig EnergyConfig,
        IReadOnlyDictionary<ItemKind, int>? InitialInventory,
        IReadOnlyDictionary<ItemKind, int>? MaxInventory);
}
