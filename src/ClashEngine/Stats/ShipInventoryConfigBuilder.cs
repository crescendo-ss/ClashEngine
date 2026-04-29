using System.Collections.Generic;
using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Reads per-ship inventory caps and initial item counts from the SubspaceServer arena config.
/// <see cref="BuildInitial"/> returns the spawn loadout (<c>InitialBurst</c> / <c>InitialRepel</c>
/// / etc.); <see cref="BuildMax"/> returns the per-item carry caps (<c>BurstMax</c> /
/// <c>RepelMax</c> / etc.). Both produce per-<see cref="ItemKind"/> dictionaries suitable for
/// <see cref="StatsRecorder.RegisterPlayer(global::ClashEngine.Core.Identity.PlayerKey, int, int, double, WeaponEnergyConfig, uint, IReadOnlyDictionary{ItemKind, int}?, IReadOnlyDictionary{ItemKind, int}?)"/>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ShipEnergyConfigBuilder"/>. Missing keys are treated as 0 (no initial
/// items / no cap of that kind). The per-life inventory tracker resets to the initial loadout
/// on every <c>OnSpawn</c>; green pickups bump it up to the cap; "wasted items" is whatever's
/// left when the player is finally knocked out or the match ends.
/// </remarks>
public static class ShipInventoryConfigBuilder
{
    public static IReadOnlyDictionary<ItemKind, int> BuildInitial(IConfigManager config, ConfigHandle ch, ShipType ship)
    {
        string section = ShipSection.Of(ship);
        int Get(string key) => config.GetInt(ch, section, key, 0);

        var dict = new Dictionary<ItemKind, int>();
        Add(dict, ItemKind.Burst, Get("InitialBurst"));
        Add(dict, ItemKind.Repel, Get("InitialRepel"));
        Add(dict, ItemKind.Thor, Get("InitialThor"));
        Add(dict, ItemKind.Brick, Get("InitialBrick"));
        Add(dict, ItemKind.Decoy, Get("InitialDecoy"));
        Add(dict, ItemKind.Portal, Get("InitialPortal"));
        Add(dict, ItemKind.Rocket, Get("InitialRocket"));
        return dict;
    }

    public static IReadOnlyDictionary<ItemKind, int> BuildMax(IConfigManager config, ConfigHandle ch, ShipType ship)
    {
        string section = ShipSection.Of(ship);
        int Get(string key) => config.GetInt(ch, section, key, 0);

        var dict = new Dictionary<ItemKind, int>();
        Add(dict, ItemKind.Burst, Get("BurstMax"));
        Add(dict, ItemKind.Repel, Get("RepelMax"));
        Add(dict, ItemKind.Thor, Get("ThorMax"));
        Add(dict, ItemKind.Brick, Get("BrickMax"));
        Add(dict, ItemKind.Decoy, Get("DecoyMax"));
        Add(dict, ItemKind.Portal, Get("PortalMax"));
        Add(dict, ItemKind.Rocket, Get("RocketMax"));
        return dict;
    }

    private static void Add(Dictionary<ItemKind, int> dict, ItemKind kind, int count)
    {
        if (count > 0) dict[kind] = count;
    }
}
