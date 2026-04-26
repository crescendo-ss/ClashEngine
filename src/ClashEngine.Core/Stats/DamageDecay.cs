using System;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Time-weighting function for damage attribution. Recent damage carries more weight than
/// older damage according to an exponential half-life: <c>weight = 0.5 ^ (dt / halfLifeTicks)</c>.
/// Used on kill and repel-use to credit attackers in proportion to their recent contribution.
/// </summary>
public sealed class DamageDecay
{
    public const double DefaultHalfLifeTicks = 200.0; // 2 seconds at 100 Hz

    public DamageDecay(double halfLifeTicks = DefaultHalfLifeTicks)
    {
        if (halfLifeTicks <= 0) throw new ArgumentOutOfRangeException(nameof(halfLifeTicks), "Must be positive.");
        HalfLifeTicks = halfLifeTicks;
    }

    public double HalfLifeTicks { get; }

    /// <summary>
    /// Multiplicative weight at <paramref name="entryTick"/> evaluated at
    /// <paramref name="currentTick"/>. Returns 1.0 for current-tick (or future) damage and
    /// decays toward 0 by the half-life curve <c>0.5 ^ (dt / halfLifeTicks)</c>.
    /// </summary>
    public double WeightAt(uint entryTick, uint currentTick) =>
        currentTick <= entryTick
            ? 1.0
            : Math.Pow(0.5, (currentTick - entryTick) / HalfLifeTicks);
}
