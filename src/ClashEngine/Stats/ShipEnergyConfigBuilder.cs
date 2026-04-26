using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Stats;

/// <summary>
/// Reads per-ship weapon energy costs out of the SubspaceServer arena config and produces a
/// <see cref="WeaponEnergyConfig"/> for the recorder. Cost keys live under each ship's section
/// (e.g. <c>[Warbird]</c>) as <c>BulletFireEnergy</c>, <c>BulletFireEnergyUpgrade</c>, etc.
/// </summary>
/// <remarks>
/// When the <c>*Upgrade</c> key is missing from the conf, the upgrade cost is defaulted to the
/// base cost -- which collapses the per-shot formula to <c>level x base</c>, matching observed
/// Continuum behavior. Detection uses a sentinel default of -1 from <c>GetInt</c>; an explicit
/// <c>0</c> in the conf is honored as zero-upgrade.
/// </remarks>
public static class ShipEnergyConfigBuilder
{
    private const int Missing = -1;

    public static WeaponEnergyConfig Build(IConfigManager config, ConfigHandle ch, ShipType ship)
    {
        string section = ShipSection.Of(ship);

        int GetOrZero(string key)
        {
            int v = config.GetInt(ch, section, key, Missing);
            return v == Missing ? 0 : v;
        }

        (int Base, int Upgrade) CostPair(string baseKey, string upgradeKey)
        {
            int b = GetOrZero(baseKey);
            int rawUp = config.GetInt(ch, section, upgradeKey, Missing);
            int up = rawUp == Missing ? b : rawUp;  // missing upgrade defaults to base
            return (b, up);
        }

        var costs = new Dictionary<WeaponKind, (int Base, int Upgrade)>
        {
            [WeaponKind.Bullet] = CostPair("BulletFireEnergy", "BulletFireEnergyUpgrade"),
            [WeaponKind.BouncingBullet] = CostPair("BulletFireEnergy", "BulletFireEnergyUpgrade"),
            [WeaponKind.Bomb] = CostPair("BombFireEnergy", "BombFireEnergyUpgrade"),
            [WeaponKind.Mine] = CostPair("LandMineFireEnergy", "LandMineFireEnergyUpgrade"),
            // Thor: flat cost per shot, same key family.
            [WeaponKind.Thor] = CostPair("BombFireEnergy", "BombFireEnergyUpgrade"),
        };

        int multifire = GetOrZero("MultiFireEnergy");
        return new WeaponEnergyConfig(costs, multifireBulletEnergy: multifire);
    }

    /// <summary>
    /// Convert SubspaceServer's <c>InitialRecharge</c> (energy per 1000 ticks) to the per-tick
    /// rate <see cref="DamageRecoveryState"/> expects.
    /// </summary>
    public static double GetRechargeRatePerTick(IConfigManager config, ConfigHandle ch, ShipType ship)
    {
        // InitialRecharge: max-energy units per ~1000 ticks (10 seconds at 100 Hz). Convert to per-tick.
        int initialRecharge = config.GetInt(ch, ShipSection.Of(ship), "InitialRecharge", 0);
        return initialRecharge / 1000.0;
    }

    public static int GetMaxEnergy(IConfigManager config, ConfigHandle ch, ShipType ship) =>
        config.GetInt(ch, ShipSection.Of(ship), "InitialEnergy", 1000);
}
