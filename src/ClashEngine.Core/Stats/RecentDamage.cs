using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Snapshot view of a single unrecovered damage instance against a player at a particular tick.
/// Returned by <see cref="DamageRecoveryState.Snapshot"/>. <see cref="Amount"/> is the residual
/// (post-recovery) damage in fractional units, since a tick of recovery may consume part of an
/// entry without retiring it.
/// </summary>
public readonly record struct RecentDamage(
    uint Tick,
    double Amount,
    WeaponKind Weapon,
    PlayerKey Attacker);
