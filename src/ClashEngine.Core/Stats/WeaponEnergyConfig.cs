using System;
using System.Collections.Generic;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Per-ship lookup of weapon energy costs. Source values come from the SubspaceServer arena
/// configuration (e.g. <c>BulletFireEnergy</c> and <c>BulletFireEnergyUpgrade</c>); the adapter
/// reads them and constructs one of these per ship type.
/// </summary>
/// <remarks>
/// <para><b>Per-shot cost.</b> Single-fire shots use <c>base + (level - 1) x upgrade</c>. When
/// the upgrade entry is missing from the arena conf the adapter substitutes
/// <c>upgrade = base</c>, which collapses the formula to <c>level x base</c> -- the empirically
/// observed Continuum behavior for unspecified upgrade keys.</para>
/// <para><b>Multifire bullets are a separate projectile.</b> A multifire shot ignores the
/// regular bullet cost entirely and uses <c>level x <see cref="MultifireBulletEnergy"/></c>
/// (e.g. a level-3 multifire bullet with <c>MultiFireEnergy=35</c> is <c>3 x 35 = 105</c>,
/// regardless of <c>BulletFireEnergy</c>). Multifire only applies to bullets -- the caller is
/// expected to set <c>multifire=true</c> only for <see cref="WeaponKind.Bullet"/> /
/// <see cref="WeaponKind.BouncingBullet"/>.</para>
/// </remarks>
public sealed class WeaponEnergyConfig
{
    private readonly Dictionary<WeaponKind, (int Base, int Upgrade)> _costs;

    public WeaponEnergyConfig(
        IReadOnlyDictionary<WeaponKind, (int Base, int Upgrade)> costs,
        int multifireBulletEnergy)
    {
        ArgumentNullException.ThrowIfNull(costs);
        if (multifireBulletEnergy < 0)
            throw new ArgumentOutOfRangeException(nameof(multifireBulletEnergy), "Must be non-negative.");

        _costs = new Dictionary<WeaponKind, (int, int)>(costs.Count);
        foreach (var kvp in costs)
        {
            if (kvp.Value.Base < 0)
                throw new ArgumentOutOfRangeException(nameof(costs), $"{kvp.Key} base cost is negative.");
            if (kvp.Value.Upgrade < 0)
                throw new ArgumentOutOfRangeException(nameof(costs), $"{kvp.Key} upgrade cost is negative.");
            _costs[kvp.Key] = kvp.Value;
        }

        MultifireBulletEnergy = multifireBulletEnergy;
    }

    /// <summary>L1 cost of a multifire bullet shot. Higher levels scale linearly.</summary>
    public int MultifireBulletEnergy { get; }

    /// <summary>
    /// Energy cost for a single shot of <paramref name="kind"/> at weapon level
    /// <paramref name="level"/> (1-3). When <paramref name="multifire"/> is true (only meaningful
    /// for bullets) the cost is <c>level x <see cref="MultifireBulletEnergy"/></c> and the
    /// regular bullet cost is bypassed. Returns 0 if no cost is configured for the weapon.
    /// </summary>
    public int EnergyForShot(WeaponKind kind, int level, bool multifire = false)
    {
        if (level < 1) throw new ArgumentOutOfRangeException(nameof(level), "Must be >= 1.");
        if (multifire) return level * MultifireBulletEnergy;
        if (!_costs.TryGetValue(kind, out var c)) return 0;
        return c.Base + (level - 1) * c.Upgrade;
    }
}
