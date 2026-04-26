namespace ClashEngine.Core.Stats;

/// <summary>
/// Weapon source for a damage event. Damage from any kind is added to raw damage totals and
/// the victim's recovery state, but only the player-aimed kinds (see
/// <see cref="WeaponKindExtensions.IsTrackedForAccuracy"/>) participate in per-weapon
/// shot/hit accounting.
/// </summary>
public enum WeaponKind
{
    Bullet,
    BouncingBullet,
    Bomb,
    BouncingBomb,
    Mine,
    BouncingMine,
    Thor,

    /// <summary>Auto-spawned from bomb detonations. Damage counts but is not credited to
    /// per-weapon accuracy (the originating bomb already counted its hits).</summary>
    Shrapnel,

    /// <summary>Bullet from a Burst item deployment. Damage counts but is not credited to
    /// per-weapon accuracy (use is tracked via <see cref="ItemKind.Burst"/>).</summary>
    Burst,
}

public static class WeaponKindExtensions
{
    /// <summary>Whether shots/hits of this kind are tracked in <see cref="PlayerStats.PerWeapon"/>.</summary>
    public static bool IsTrackedForAccuracy(this WeaponKind kind) =>
        kind != WeaponKind.Shrapnel && kind != WeaponKind.Burst;

    /// <summary>Whether this weapon's hits are produced by splash/proximity (and thus a single
    /// shot may produce multiple hit events).</summary>
    public static bool IsSplash(this WeaponKind kind) => kind switch
    {
        WeaponKind.Bomb or WeaponKind.BouncingBomb
            or WeaponKind.Mine or WeaponKind.BouncingMine
            or WeaponKind.Thor => true,
        _ => false,
    };
}
