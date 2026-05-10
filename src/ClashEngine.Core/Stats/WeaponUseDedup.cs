using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Per-player sliding-window log of recent (weapon, tick) pairs used to drop duplicate
/// weapon events delivered by the wire layer. Mirrors <c>SS.Matchmaking.TeamVersusStats</c>'s
/// approach (TrimWeaponUseLog + AddWeaponUse): Continuum / the SS network layer occasionally
/// redelivers a position packet, and without this gate every redelivery double-counts the
/// event the packet carried -- bullet/bomb fire counts, item-press counters, and especially
/// forced-repel credit, where the per-event invariant (sum of credits == 1.0) makes the
/// resulting overcount glaringly visible on the post-match scoreboard.
/// </summary>
public sealed class WeaponUseDedup
{
    /// <summary>
    /// Trim window in client ticks (100 Hz). Matches TeamVersusStats's 5-second window so an
    /// arbitrarily-late redelivery stays bounded but legitimate same-weapon presses spaced
    /// apart by &gt;5 s are never collapsed.
    /// </summary>
    public const uint WindowTicks = 500;

    private readonly Dictionary<PlayerKey, List<(byte Weapon, uint Tick)>> _logs = new();

    /// <summary>
    /// Returns <c>true</c> if the (<paramref name="weaponType"/>, <paramref name="atTick"/>)
    /// pair is new for <paramref name="player"/> (caller should process the event), or
    /// <c>false</c> if it duplicates an entry already in the trim window (caller should drop).
    /// </summary>
    public bool Observe(PlayerKey player, byte weaponType, uint atTick)
    {
        if (!_logs.TryGetValue(player, out var log))
        {
            log = new List<(byte, uint)>();
            _logs[player] = log;
        }

        // Saturating cutoff so a client-tick wraparound doesn't evict the entire log.
        uint cutoff = atTick > WindowTicks ? atTick - WindowTicks : 0u;
        for (int i = log.Count - 1; i >= 0; i--)
            if (log[i].Tick < cutoff) log.RemoveAt(i);

        for (int i = 0; i < log.Count; i++)
            if (log[i].Weapon == weaponType && log[i].Tick == atTick) return false;

        log.Add((weaponType, atTick));
        return true;
    }

    /// <summary>Drops the dedup log for <paramref name="player"/>.</summary>
    public void Forget(PlayerKey player) => _logs.Remove(player);

    /// <summary>Drops every player's dedup log.</summary>
    public void Clear() => _logs.Clear();
}
