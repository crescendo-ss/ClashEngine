using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Per-player live stats aggregate for one match. Counters mutate in response to match events
/// dispatched by <see cref="StatsRecorder"/>. State spans the whole match (not reset on ship
/// change) and survives slot replacement only by being closed at <c>OnLeaveMatch</c> -- a new
/// occupant of the same slot gets their own <see cref="PlayerStats"/>.
/// </summary>
/// <remarks>
/// <para>Mutators are <c>internal</c> so the recorder is the only writer. External readers see
/// immutable views via the public properties.</para>
/// </remarks>
public sealed class PlayerStats
{
    private readonly Dictionary<WeaponKind, WeaponShotStats> _perWeapon = new();
    private readonly Dictionary<ItemKind, int> _itemUses = new();
    private readonly Dictionary<ItemKind, int> _wastedItems = new();
    private readonly Dictionary<ItemKind, int> _inventory = new();
    private readonly List<LifeStats> _lives = new();
    private readonly List<DistanceSample> _distanceSamples = new();

    public PlayerStats(PlayerKey player, DamageRecoveryState recovery)
    {
        if (player.IsDefault) throw new ArgumentException("Player must have a non-default key.", nameof(player));
        ArgumentNullException.ThrowIfNull(recovery);
        Player = player;
        Recovery = recovery;
    }

    public PlayerKey Player { get; }

    /// <summary>Per-victim recovery state owning the FIFO of unrecovered damage taken by this player.</summary>
    public DamageRecoveryState Recovery { get; }

    public int Kills { get; private set; }
    public int Deaths { get; private set; }

    /// <summary>Number of opponents this player eliminated -- kills that closed the victim's
    /// last life. Credited to the killer (not the victim), mirroring <see cref="Kills"/>.
    /// Teamkills do not count.</summary>
    public int Knockouts { get; private set; }

    public int Assists { get; private set; }
    public int Teamkills { get; private set; }

    public long DamageDealt { get; private set; }
    public long DamageTaken { get; private set; }
    public long SelfDamage { get; private set; }
    public long TeamDamageDealt { get; private set; }
    public long TeamDamageTaken { get; private set; }

    /// <summary>
    /// Raw (non-decay-weighted) damage this player contributed to triggering victim deaths.
    /// Each kill snapshots the victim's recovery state (damage instances not yet recharged
    /// away) and adds the per-attacker recovery-adjusted amounts to this counter. Aggregated
    /// across every kill the player contributed to.
    /// </summary>
    public double KillDamage { get; private set; }

    /// <summary>
    /// Fractional kill participation: each death distributes 1.0 of credit across contributing
    /// attackers in proportion to their decay-weighted damage share. Sum across all attackers
    /// for a single kill is 1.0; this player's tally accumulates only their share. Analogous to
    /// <see cref="ForcedRepelCredit"/> but triggered by deaths rather than repels.
    /// </summary>
    public double KillCredit { get; private set; }

    /// <summary>
    /// Raw (non-decay-weighted) damage this player contributed to triggering enemy repels.
    /// Each repel snapshots the user's recovery state and adds the per-attacker recovery-adjusted
    /// amounts to this counter.
    /// </summary>
    public double ForcedRepelDamage { get; private set; }

    /// <summary>
    /// Fractional forced-repel participation: each repel distributes 1.0 of credit across
    /// contributing attackers in proportion to their decay-weighted damage share. Sum across all
    /// attackers for a single repel is 1.0; this player's tally accumulates only their share.
    /// Analogous to <see cref="KillCredit"/> but triggered by repels rather than deaths.
    /// </summary>
    public double ForcedRepelCredit { get; private set; }

    public uint ActiveTicks { get; private set; }

    public IReadOnlyDictionary<WeaponKind, WeaponShotStats> PerWeapon => _perWeapon;
    public IReadOnlyDictionary<ItemKind, int> ItemUses => _itemUses;

    /// <summary>
    /// Items the player took to the grave on closed-out lives -- initial loadout + green pickups
    /// − uses, snapshotted at each death (or other elimination event). Survivors who reach
    /// match-end with items still in hand do not have those items folded in here, since the
    /// "wasted" measure is meant to quantify items that were spent without being used in the
    /// course of dying, not leftover stockpile from a life the player kept. Populated
    /// automatically by the recorder via <see cref="SnapshotInventoryAsWasted"/>; may also be
    /// set externally via <see cref="SetWastedItem"/>.
    /// </summary>
    public IReadOnlyDictionary<ItemKind, int> WastedItems => _wastedItems;

    /// <summary>
    /// Currently-held item inventory for this player. Reset to the ship's initial loadout on
    /// every <c>OnSpawn</c>, decremented on <c>OnItemUsed</c>, and incremented on
    /// <c>OnPrizePickup</c> (clamped at the ship's per-item cap).
    /// </summary>
    public IReadOnlyDictionary<ItemKind, int> Inventory => _inventory;

    public IReadOnlyList<LifeStats> Lives => _lives;

    /// <summary>
    /// Periodic distance samples to the nearest enemy player. Driven by the host on a fixed
    /// cadence (default 5 Hz, configurable). Distance is in pixels (SubspaceServer's native
    /// position unit). Samples are appended in chronological order while the player is in-arena.
    /// </summary>
    public IReadOnlyList<DistanceSample> DistanceSamples => _distanceSamples;

    /// <summary>The currently-open life, or null if the player is between lives or never spawned.</summary>
    public LifeStats? CurrentLife => _lives.Count > 0 && !_lives[^1].IsClosed ? _lives[^1] : null;

    // --- internal mutators (called by StatsRecorder) ---

    internal void RecordKill()
    {
        Kills++;
        CurrentLife?.RecordKill();
    }

    internal void RecordDeath() => Deaths++;
    internal void RecordKnockout() => Knockouts++;
    internal void RecordAssist() => Assists++;
    internal void RecordTeamkill() => Teamkills++;

    internal void AddDamageDealt(int amount)
    {
        DamageDealt += amount;
        CurrentLife?.AddDamageDealt(amount);
    }

    internal void AddDamageTaken(int amount)
    {
        DamageTaken += amount;
        CurrentLife?.AddDamageTaken(amount);
    }

    internal void AddSelfDamage(int amount) => SelfDamage += amount;
    internal void AddTeamDamageDealt(int amount) => TeamDamageDealt += amount;
    internal void AddTeamDamageTaken(int amount) => TeamDamageTaken += amount;

    internal void AddKillDamage(double amount) => KillDamage += amount;
    internal void AddKillCredit(double amount) => KillCredit += amount;
    internal void AddForcedRepelDamage(double amount) => ForcedRepelDamage += amount;
    internal void AddForcedRepelCredit(double amount) => ForcedRepelCredit += amount;

    internal void AddActiveTicks(uint ticks) => ActiveTicks += ticks;

    internal WeaponShotStats GetOrCreateWeapon(WeaponKind kind)
    {
        if (!_perWeapon.TryGetValue(kind, out var s))
        {
            s = new WeaponShotStats();
            _perWeapon[kind] = s;
        }
        return s;
    }

    internal void RecordItemUse(ItemKind item)
    {
        _itemUses[item] = _itemUses.TryGetValue(item, out var n) ? n + 1 : 1;
    }

    /// <summary>Set the count of items left in inventory at the end of the player's match.
    /// Idempotent overwrite; the host calls this once per item kind after the final knockout
    /// or at match end.</summary>
    internal void SetWastedItem(ItemKind item, int count)
    {
        if (count > 0) _wastedItems[item] = count;
        else _wastedItems.Remove(item);
    }

    /// <summary>Reset the player's inventory to the ship's initial loadout. Called on each
    /// spawn so the inventory tracks the current life. <paramref name="initial"/> may be null
    /// or empty (no items at all).</summary>
    internal void ResetInventory(IReadOnlyDictionary<ItemKind, int>? initial)
    {
        _inventory.Clear();
        if (initial is null) return;
        foreach (var (item, count) in initial)
            if (count > 0) _inventory[item] = count;
    }

    /// <summary>Set an item's inventory count to an absolute value. Wire-authoritative writer used
    /// by the host adapter to mirror the count Continuum reports in <c>ExtraPositionData</c> on
    /// every position packet -- the client is the source of truth, not a reconstructive event
    /// stream. <paramref name="count"/> &lt;= 0 removes the entry.</summary>
    internal void SetInventoryItem(ItemKind item, int count)
    {
        if (count > 0) _inventory[item] = count;
        else _inventory.Remove(item);
    }

    /// <summary>Accumulate the current life's leftover inventory into <see cref="WastedItems"/>
    /// and clear the live inventory. Called at death/leave -- not at match-end -- since the
    /// "wasted" tally counts items the player carried into a death, not the surviving-final-life
    /// stockpile. Multi-life matches sum the leftover from each closed life. Calling twice in a
    /// row with no pickups in between is a no-op since the second call sees an empty inventory.</summary>
    internal void SnapshotInventoryAsWasted()
    {
        if (_inventory.Count == 0) return;
        foreach (var (item, count) in _inventory)
        {
            if (count <= 0) continue;
            _wastedItems[item] = _wastedItems.TryGetValue(item, out var existing)
                ? existing + count
                : count;
        }
        _inventory.Clear();
    }

    internal void AppendDistanceSample(uint atTick, float distancePixels)
    {
        if (distancePixels < 0) distancePixels = 0;
        _distanceSamples.Add(new DistanceSample(atTick, distancePixels));
    }

    internal LifeStats OpenLife(uint startTick)
    {
        var open = CurrentLife;
        if (open is not null) return open; // idempotent -- caller may double-fire on edge cases
        var life = new LifeStats(startTick);
        _lives.Add(life);
        return life;
    }

    internal void CloseLife(uint endTick, LifeEndReason reason, PlayerKey? knockoutBy, string? shipAtEnd)
    {
        var open = CurrentLife;
        open?.Close(endTick, reason, knockoutBy, shipAtEnd);
    }
}
