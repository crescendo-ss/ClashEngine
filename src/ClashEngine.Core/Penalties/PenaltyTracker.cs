using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Penalties;

/// <summary>
/// Tracks per-player, per-kind penalty history. Each kind has its own escalation ladder.
/// Effective timeout is the maximum across all kinds.
/// </summary>
/// <remarks>
/// <para>
/// Stores raw penalty events (not aggregated state) so vetoes can <see cref="RescindMostRecent"/>
/// the latest entry without losing historical fidelity.
/// </para>
/// <para>
/// Thread-safe: every public mutation and read is guarded by a single internal lock. The matchmaking
/// engine drives this on the mainloop thread, while the persistence layer reads/writes from a worker
/// thread on player login/logoff. Critical sections are short (Dictionary/List ops only).
/// </para>
/// <para>
/// Each event carries a severity multiplier. The active timeout is computed from the most
/// recent event's severity multiplied by the escalating base timeout:
/// <c>timeoutEnd = latest.At + BaseTimeout * EscalationFactor^(N-1) * latest.Severity</c>.
/// </para>
/// </remarks>
public sealed class PenaltyTracker
{
    private readonly Dictionary<(PlayerKey Player, PenaltyKind Kind), List<Event>> _events = new();
    private readonly Dictionary<PenaltyKind, PenaltyPolicy> _policies;
    private readonly object _gate = new();

    private readonly record struct Event(DateTimeOffset At, double Severity, TimeSpan? BaseTimeoutOverride);

    public PenaltyTracker(params PenaltyPolicy[] policies)
        : this((IEnumerable<PenaltyPolicy>)policies) { }

    public PenaltyTracker(IEnumerable<PenaltyPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = new Dictionary<PenaltyKind, PenaltyPolicy>();
        foreach (var p in policies)
        {
            if (_policies.ContainsKey(p.Kind))
                throw new ArgumentException($"Duplicate policy for {p.Kind}.", nameof(policies));
            _policies[p.Kind] = p;
        }
    }

    public PenaltyPolicy GetPolicy(PenaltyKind kind) =>
        _policies.TryGetValue(kind, out var p)
            ? p
            : throw new InvalidOperationException($"No policy registered for {kind}.");

    public bool HasPolicy(PenaltyKind kind) => _policies.ContainsKey(kind);

    public int RecordPenalty(PlayerKey player, PenaltyKind kind, DateTimeOffset at, double severity = 1.0,
        TimeSpan? baseTimeoutOverride = null)
    {
        if (player.IsDefault) throw new ArgumentException("Player must not be default.", nameof(player));
        if (!_policies.ContainsKey(kind)) throw new InvalidOperationException($"No policy registered for {kind}.");
        if (severity < 1.0) throw new ArgumentOutOfRangeException(nameof(severity), "Must be >= 1.");
        if (baseTimeoutOverride is { } bto && bto < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseTimeoutOverride), "Must be non-negative when specified.");

        lock (_gate)
        {
            var key = (player, kind);
            if (!_events.TryGetValue(key, out var list))
            {
                list = new List<Event>();
                _events[key] = list;
            }
            list.Add(new Event(at, severity, baseTimeoutOverride));
            list.Sort(static (a, b) => a.At.CompareTo(b.At));
            return ComputeOffenseCountLocked(list, kind);
        }
    }

    public bool RescindMostRecent(PlayerKey player, PenaltyKind kind)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue((player, kind), out var list) || list.Count == 0)
                return false;
            list.RemoveAt(list.Count - 1);
            if (list.Count == 0) _events.Remove((player, kind));
            return true;
        }
    }

    public int GetOffenseCount(PlayerKey player, PenaltyKind kind)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue((player, kind), out var list) || list.Count == 0) return 0;
            return ComputeOffenseCountLocked(list, kind);
        }
    }

    public DateTimeOffset? TimeoutEndForKind(PlayerKey player, PenaltyKind kind)
    {
        lock (_gate)
        {
            return TimeoutEndForKindLocked(player, kind);
        }
    }

    public DateTimeOffset? TimeoutUntil(PlayerKey player)
    {
        lock (_gate)
        {
            DateTimeOffset? max = null;
            foreach (var kind in _policies.Keys)
            {
                var end = TimeoutEndForKindLocked(player, kind);
                if (end is null) continue;
                if (max is null || end > max) max = end;
            }
            return max;
        }
    }

    public bool IsInTimeout(PlayerKey player, DateTimeOffset at)
    {
        var until = TimeoutUntil(player);
        return until.HasValue && until.Value > at;
    }

    public IReadOnlyList<PenaltyRecord> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<PenaltyRecord>();
            foreach (var kvp in _events)
                foreach (var ev in kvp.Value)
                    list.Add(new PenaltyRecord(kvp.Key.Player, kvp.Key.Kind, ev.At, ev.Severity, ev.BaseTimeoutOverride));
            return list;
        }
    }

    public void Rehydrate(IEnumerable<PenaltyRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            _events.Clear();
            foreach (var r in records)
            {
                if (r.Player.IsDefault) continue;
                if (r.Severity < 1.0) continue;
                var key = (r.Player, r.Kind);
                if (!_events.TryGetValue(key, out var list))
                {
                    list = new List<Event>();
                    _events[key] = list;
                }
                list.Add(new Event(r.At, r.Severity, r.BaseTimeoutOverride));
            }
            foreach (var list in _events.Values)
                list.Sort(static (a, b) => a.At.CompareTo(b.At));
        }
    }

    /// <summary>
    /// Atomically replaces every event for <paramref name="player"/> across all kinds with the
    /// supplied <paramref name="records"/>. Records belonging to other players or to unregistered
    /// kinds are ignored. This is the per-player counterpart to <see cref="Rehydrate"/> and
    /// exists so the persistence layer can re-load a player's penalty history on login without
    /// double-counting events that the tracker still holds in memory from a previous session
    /// (the bug that produced the ~8898h penalty seen in production).
    /// </summary>
    public void ReplacePlayerHistory(PlayerKey player, IEnumerable<PenaltyRecord> records)
    {
        if (player.IsDefault) throw new ArgumentException("Player must not be default.", nameof(player));
        ArgumentNullException.ThrowIfNull(records);

        lock (_gate)
        {
            List<(PlayerKey, PenaltyKind)>? toRemove = null;
            foreach (var k in _events.Keys)
            {
                if (k.Player.Equals(player))
                    (toRemove ??= new List<(PlayerKey, PenaltyKind)>()).Add(k);
            }
            if (toRemove is not null)
                foreach (var k in toRemove) _events.Remove(k);

            foreach (var r in records)
            {
                if (!r.Player.Equals(player)) continue;
                if (!_policies.ContainsKey(r.Kind)) continue;
                if (r.Severity < 1.0) continue;
                var key = (r.Player, r.Kind);
                if (!_events.TryGetValue(key, out var list))
                {
                    list = new List<Event>();
                    _events[key] = list;
                }
                list.Add(new Event(r.At, r.Severity, r.BaseTimeoutOverride));
            }

            foreach (var kvp in _events)
            {
                if (kvp.Key.Player.Equals(player))
                    kvp.Value.Sort(static (a, b) => a.At.CompareTo(b.At));
            }
        }
    }

    public int Prune(DateTimeOffset at)
    {
        lock (_gate)
        {
            int removed = 0;
            var keysToRemove = new List<(PlayerKey, PenaltyKind)>();
            foreach (var kvp in _events)
            {
                var policy = GetPolicy(kvp.Key.Kind);
                int count = ComputeOffenseCountLocked(kvp.Value, kvp.Key.Kind);
                var latest = kvp.Value[^1];
                var timeoutEnd = latest.At + policy.EffectiveTimeoutFor(count, latest.Severity, latest.BaseTimeoutOverride);
                var memoryEnd = latest.At + policy.MemoryWindow;
                if (timeoutEnd <= at && memoryEnd <= at)
                {
                    keysToRemove.Add(kvp.Key);
                    removed += kvp.Value.Count;
                }
            }
            foreach (var k in keysToRemove) _events.Remove(k);
            return removed;
        }
    }

    private DateTimeOffset? TimeoutEndForKindLocked(PlayerKey player, PenaltyKind kind)
    {
        if (!_events.TryGetValue((player, kind), out var list) || list.Count == 0) return null;
        int count = ComputeOffenseCountLocked(list, kind);
        if (count == 0) return null;
        var policy = GetPolicy(kind);
        var latest = list[^1];
        return latest.At + policy.EffectiveTimeoutFor(count, latest.Severity, latest.BaseTimeoutOverride);
    }

    private int ComputeOffenseCountLocked(List<Event> sortedEvents, PenaltyKind kind)
    {
        if (sortedEvents.Count == 0) return 0;
        var policy = GetPolicy(kind);
        int count = 1;
        for (int i = sortedEvents.Count - 2; i >= 0; i--)
        {
            if (sortedEvents[i + 1].At - sortedEvents[i].At < policy.MemoryWindow)
                count++;
            else
                break;
        }
        return count;
    }
}
