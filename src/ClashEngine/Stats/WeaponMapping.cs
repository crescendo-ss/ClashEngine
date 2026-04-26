using ClashEngine.Core.Stats;
using SS.Packets.Game;

namespace ClashEngine.Stats;

/// <summary>
/// Translates SubspaceServer wire-level <see cref="WeaponCodes"/> + <see cref="WeaponData"/>
/// flags to ClashEngine's <see cref="WeaponKind"/>.
/// </summary>
public static class WeaponMapping
{
    /// <summary>
    /// Map a damage-side weapon to a stats-tracked kind. Wormhole self-damage and Null
    /// produce no kind -- caller should skip those.
    /// </summary>
    public static WeaponKind? FromDamage(in WeaponData weapon)
    {
        return weapon.Type switch
        {
            WeaponCodes.Bullet => WeaponKind.Bullet,
            WeaponCodes.BounceBullet => WeaponKind.BouncingBullet,
            WeaponCodes.Bomb or WeaponCodes.ProxBomb => weapon.Alternate ? WeaponKind.Mine : WeaponKind.Bomb,
            WeaponCodes.Thor => WeaponKind.Thor,
            WeaponCodes.Burst => WeaponKind.Burst,
            WeaponCodes.Shrapnel => WeaponKind.Shrapnel,
            _ => null, // Repel/Decoy don't produce damage; Null/Wormhole aren't tracked here
        };
    }

    /// <summary>
    /// Map a position-packet weapon field to an event the recorder cares about. The result is
    /// either a weapon fire (with multifire flag for bullets) or an item use (repel, burst,
    /// decoy), or null (no relevant event this packet).
    /// </summary>
    /// <remarks>
    /// The wire's <see cref="WeaponData.Alternate"/> bit is reused per weapon type: for
    /// bombs/prox-bombs it distinguishes Mine from Bomb; for bullets it indicates a multifire
    /// shot. The recorder uses the multifire flag to add the per-shot
    /// <c>MultiFireEnergy</c> surcharge.
    /// </remarks>
    public static PositionEventKind FromPositionPacket(in WeaponData weapon)
    {
        return weapon.Type switch
        {
            WeaponCodes.Bullet => PositionEventKind.WeaponFire(WeaponKind.Bullet, weapon.Alternate),
            WeaponCodes.BounceBullet => PositionEventKind.WeaponFire(WeaponKind.BouncingBullet, weapon.Alternate),
            WeaponCodes.Bomb or WeaponCodes.ProxBomb =>
                PositionEventKind.WeaponFire(weapon.Alternate ? WeaponKind.Mine : WeaponKind.Bomb, multifire: false),
            WeaponCodes.Thor => PositionEventKind.WeaponFire(WeaponKind.Thor, multifire: false),
            WeaponCodes.Repel => PositionEventKind.ItemUse(ItemKind.Repel),
            WeaponCodes.Burst => PositionEventKind.ItemUse(ItemKind.Burst),
            WeaponCodes.Decoy => PositionEventKind.ItemUse(ItemKind.Decoy),
            _ => default,
        };
    }
}

/// <summary>Tagged result of <see cref="WeaponMapping.FromPositionPacket"/>.</summary>
public readonly struct PositionEventKind
{
    public readonly WeaponKind? Weapon;
    public readonly ItemKind? Item;
    public readonly bool Multifire;

    private PositionEventKind(WeaponKind? weapon, ItemKind? item, bool multifire)
    {
        Weapon = weapon; Item = item; Multifire = multifire;
    }

    public static PositionEventKind WeaponFire(WeaponKind w, bool multifire) => new(w, null, multifire);
    public static PositionEventKind ItemUse(ItemKind i) => new(null, i, false);

    public bool IsWeaponFire => Weapon.HasValue;
    public bool IsItemUse => Item.HasValue;
    public bool IsNone => !Weapon.HasValue && !Item.HasValue;
}
