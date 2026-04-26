using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Resolves the EMP shutdown duration (in ticks) for a damage event. Reads the per-ship
/// <c>EmpBomb</c> flag and the arena-level <c>Bomb:EBombShutdownTime</c>; returns 0 when the
/// attacker's ship is not configured for EMP bombs or the damage didn't come from a bomb.
/// </summary>
/// <remarks>
/// The lookup is best-effort: a stronger overlapping EMP is handled correctly by
/// <see cref="ClashEngine.Core.Stats.DamageRecoveryState"/>'s max-rule, and a non-EMP ship's
/// bombs simply yield 0 here. Per-level distinctions are not currently considered -- the
/// per-ship <c>EmpBomb</c> flag is treated as applying to all of that ship's bombs.
/// </remarks>
public sealed class EmpShutdownLookup
{
    private readonly IConfigManager _config;

    public EmpShutdownLookup(IConfigManager config)
    {
        _config = config;
    }

    public uint For(Arena? arena, ShipType attackerShip, SS.Packets.Game.WeaponCodes weapon)
    {
        if (arena is null) return 0;
        if (weapon is not (SS.Packets.Game.WeaponCodes.Bomb or SS.Packets.Game.WeaponCodes.ProxBomb))
            return 0;

        var ch = arena.Cfg;
        if (ch is null) return 0;

        bool isEmp = _config.GetInt(ch, ShipSection.Of(attackerShip), "EmpBomb", 0) != 0;
        if (!isEmp) return 0;

        int shutdown = _config.GetInt(ch, "Bomb", "EBombShutdownTime", 0);
        return shutdown > 0 ? (uint)shutdown : 0;
    }
}
