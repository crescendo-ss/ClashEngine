using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Pure functions that partition a victim's recent (unrecovered) damage among the attackers
/// responsible for it. Used for kill credit (on victim death) and forced-repel credit (on
/// victim repel use). Operates on the snapshot view emitted by
/// <see cref="DamageRecoveryState.Snapshot"/>.
/// </summary>
public static class DamageAttribution
{
    /// <summary>
    /// Single pass returning both raw (recovery-adjusted, non-weighted) and decay-weighted
    /// totals per attacker. Used by the kill / forced-repel paths so they can record raw damage
    /// to the participant's tally and a decay-weighted fractional credit (share of the event's
    /// total weighted damage) in one walk over <paramref name="entries"/>.
    /// </summary>
    public static Dictionary<PlayerKey, AttributedDamage> AttributeRawAndWeighted(
        IEnumerable<RecentDamage> entries,
        uint currentTick,
        DamageDecay decay)
    {
        var result = new Dictionary<PlayerKey, AttributedDamage>();
        foreach (var e in entries)
        {
            if (e.Amount <= 0) continue;
            double weighted = e.Amount * decay.WeightAt(e.Tick, currentTick);
            if (result.TryGetValue(e.Attacker, out var existing))
                result[e.Attacker] = new AttributedDamage(
                    Raw: existing.Raw + e.Amount,
                    Weighted: existing.Weighted + weighted);
            else
                result[e.Attacker] = new AttributedDamage(Raw: e.Amount, Weighted: weighted);
        }
        return result;
    }
}

/// <summary>
/// Per-attacker damage attribution split: <see cref="Raw"/> is the unrecovered damage amount
/// (no decay), <see cref="Weighted"/> is that amount multiplied by the time-decay weight.
/// </summary>
public readonly record struct AttributedDamage(double Raw, double Weighted);
