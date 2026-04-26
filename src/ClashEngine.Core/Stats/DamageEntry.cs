using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Stats;

/// <summary>
/// A single instance of damage taken by a player from another player. The input event consumed
/// by <see cref="DamageRecoveryState"/>, which integrates this against the victim's recharge
/// rate and EMP timer to determine how much remains unrecovered at any later tick.
/// </summary>
/// <param name="Tick">Server tick at which the damage landed (10 ms granularity).</param>
/// <param name="Amount">Raw damage as reported by the victim's client.</param>
/// <param name="Weapon">Weapon that produced this damage. Drives splash-vs-direct accounting.</param>
/// <param name="EmpDurationTicks">
/// Duration of the EMP shutdown caused by <em>this hit</em>, in ticks. Zero for non-EMP weapons.
/// EMP shutdowns are not additive across overlapping hits: the recovery state takes the
/// <em>max</em> of the existing shutdown and the new one (a stronger EMP extends the timer; a
/// weaker one is absorbed without effect).
/// </param>
/// <param name="Attacker">Player who dealt the damage.</param>
public readonly record struct DamageEntry(
    uint Tick,
    short Amount,
    WeaponKind Weapon,
    uint EmpDurationTicks,
    PlayerKey Attacker);
