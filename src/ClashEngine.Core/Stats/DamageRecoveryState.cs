using System;
using System.Collections.Generic;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Per-player state machine tracking which damage instances have not yet been recovered by the
/// player's energy regeneration. Combines a bounded FIFO of damage entries (each with a mutable
/// residual amount) with the player's EMP shutdown timer and recharge rate.
/// </summary>
/// <remarks>
/// <para><b>Recovery model.</b> Energy regenerates at <see cref="RechargeRate"/> damage points
/// per tick whenever the player is not EMP-shutdown. Recovered energy retires the oldest damage
/// entry first (leaky bucket): if a tick's worth of recharge exceeds the head's residual, the
/// head is removed and the surplus rolls onto the next entry. Excess recharge after the queue
/// is empty is discarded -- the player is at full energy.</para>
/// <para><b>EMP timer.</b> <see cref="EmpEndTick"/> is the absolute tick at which recharge
/// resumes. A new EMP-causing damage extends the timer with the <em>max</em> of the existing
/// remaining shutdown and the new shutdown -- never sums them (a weaker overlapping EMP has no
/// effect). Recovery is suspended in any [now, <see cref="EmpEndTick"/>] interval.</para>
/// <para><b>Capacity bound.</b> The FIFO is capped (default 100 entries) as a defense against
/// pathological focus-fire from many sources. Eviction at capacity drops the oldest entry by
/// insertion order, regardless of remaining amount.</para>
/// <para><b>Threading.</b> Single-threaded -- accessed only from the mainloop thread.</para>
/// </remarks>
public sealed class DamageRecoveryState
{
    public const int DefaultCapacity = 100;

    private readonly LinkedList<MutableEntry> _entries = new();
    private readonly int _capacity;
    private double _rechargeRate;
    private uint _lastUpdateTick;
    private uint _empEndTick;

    public DamageRecoveryState(double rechargeRate, int capacity = DefaultCapacity, uint initialTick = 0)
    {
        if (rechargeRate < 0) throw new ArgumentOutOfRangeException(nameof(rechargeRate), "Must be non-negative.");
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "Must be positive.");

        _rechargeRate = rechargeRate;
        _capacity = capacity;
        _lastUpdateTick = initialTick;
        _empEndTick = initialTick;
    }

    public int Capacity => _capacity;
    public int Count => _entries.Count;
    public uint LastUpdateTick => _lastUpdateTick;
    public uint EmpEndTick => _empEndTick;
    public double RechargeRate => _rechargeRate;

    /// <summary>
    /// Update the recharge rate (e.g. when the player switches ships). Recovery up to the
    /// current tick is applied at the <em>old</em> rate before the new rate takes effect.
    /// </summary>
    public void SetRechargeRate(double rechargeRate, uint currentTick)
    {
        if (rechargeRate < 0) throw new ArgumentOutOfRangeException(nameof(rechargeRate), "Must be non-negative.");
        Advance(currentTick);
        _rechargeRate = rechargeRate;
    }

    /// <summary>
    /// Ingest a damage event. Recovery is advanced to <paramref name="entry"/>'s tick first,
    /// then the entry is appended (evicting the oldest if at capacity), then the EMP timer
    /// is extended if the new shutdown is stronger than what's already active.
    /// </summary>
    public void OnDamage(in DamageEntry entry)
    {
        if (entry.Amount <= 0) return; // ignore zero/negative damage events

        Advance(entry.Tick);

        if (_entries.Count == _capacity)
            _entries.RemoveFirst();
        _entries.AddLast(new MutableEntry(entry, entry.Amount));

        if (entry.EmpDurationTicks > 0)
        {
            uint candidateEnd = entry.Tick + entry.EmpDurationTicks;
            if (candidateEnd > _empEndTick)
                _empEndTick = candidateEnd;
        }
    }

    /// <summary>
    /// Advance recovery to <paramref name="currentTick"/> and return the entries that still
    /// have residual amount. Each <see cref="RecentDamage.Amount"/> is the post-recovery
    /// fractional amount. Yields oldest-first.
    /// </summary>
    public IReadOnlyList<RecentDamage> Snapshot(uint currentTick)
    {
        Advance(currentTick);
        var result = new List<RecentDamage>(_entries.Count);
        foreach (var e in _entries)
            result.Add(new RecentDamage(e.Source.Tick, e.Remaining, e.Source.Weapon, e.Source.Attacker));
        return result;
    }

    /// <summary>Drop all damage entries (e.g. on death). Leaves the EMP timer untouched.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// Advance internal state to <paramref name="toTick"/>: apply leaky-bucket recovery for the
    /// effective recharge time since <see cref="LastUpdateTick"/>, excluding any portion that
    /// overlaps the EMP shutdown window.
    /// </summary>
    private void Advance(uint toTick)
    {
        // Defensive clamp: out-of-order events shouldn't run the clock backward.
        if (toTick <= _lastUpdateTick)
        {
            _lastUpdateTick = toTick > _lastUpdateTick ? toTick : _lastUpdateTick;
            return;
        }

        // Recharge runs from max(lastUpdateTick, empEndTick) to toTick.
        uint effectiveStart = _empEndTick > _lastUpdateTick ? _empEndTick : _lastUpdateTick;
        if (toTick > effectiveStart && _entries.Count > 0 && _rechargeRate > 0)
        {
            double capacity = (toTick - effectiveStart) * _rechargeRate;
            ApplyRecovery(capacity);
        }
        _lastUpdateTick = toTick;
    }

    private void ApplyRecovery(double capacity)
    {
        var node = _entries.First;
        while (node is not null && capacity > 0)
        {
            ref var entry = ref node.ValueRef;
            if (entry.Remaining > capacity)
            {
                entry.Remaining -= capacity;
                return;
            }
            capacity -= entry.Remaining;
            var next = node.Next;
            _entries.Remove(node);
            node = next;
        }
    }

    private struct MutableEntry
    {
        public DamageEntry Source;
        public double Remaining;

        public MutableEntry(DamageEntry source, double remaining)
        {
            Source = source;
            Remaining = remaining;
        }
    }
}
