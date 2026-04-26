using System.Collections.Generic;
using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Reads per-ship initial item counts from the SubspaceServer arena config and produces a
/// per-<see cref="ItemKind"/> dictionary suitable for
/// <see cref="StatsRecorder.RegisterPlayer(global::ClashEngine.Core.Identity.PlayerKey, int, int, double, WeaponEnergyConfig, uint, IReadOnlyDictionary{ItemKind, int}?)"/>.
/// Reads <c>InitialBurst</c>, <c>InitialRepel</c>, <c>InitialThor</c>, <c>InitialBrick</c>,
/// <c>InitialDecoy</c>, <c>InitialPortal</c>, <c>InitialRocket</c> from the ship's section.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ShipEnergyConfigBuilder"/>. Missing keys are treated as 0 (no initial
/// items of that kind). The per-life inventory tracker resets to this loadout on every
/// <c>OnSpawn</c>; "wasted items" is whatever's left when the player is finally knocked out
/// or the match ends.
/// </remarks>
public static class ShipInventoryConfigBuilder
{
    public static IReadOnlyDictionary<ItemKind, int> Build(IConfigManager config, ConfigHandle ch, ShipType ship)
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

    private static void Add(Dictionary<ItemKind, int> dict, ItemKind kind, int count)
    {
        if (count > 0) dict[kind] = count;
    }
}
