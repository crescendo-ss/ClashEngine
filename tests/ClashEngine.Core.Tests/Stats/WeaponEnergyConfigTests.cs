using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class WeaponEnergyConfigTests
{
    private static WeaponEnergyConfig Build(
        int bullet = 0, int bulletUp = 0,
        int bomb = 0, int bombUp = 0,
        int mine = 0, int mineUp = 0,
        int multifire = 0)
    {
        var costs = new Dictionary<WeaponKind, (int, int)>
        {
            [WeaponKind.Bullet] = (bullet, bulletUp),
            [WeaponKind.Bomb] = (bomb, bombUp),
            [WeaponKind.Mine] = (mine, mineUp),
        };
        return new WeaponEnergyConfig(costs, multifireBulletEnergy: multifire);
    }

    [Fact]
    public void Level_one_returns_base_cost()
    {
        var cfg = Build(bullet: 100, bulletUp: 50);
        Assert.Equal(100, cfg.EnergyForShot(WeaponKind.Bullet, level: 1));
    }

    [Fact]
    public void Higher_level_adds_upgrade_per_step()
    {
        var cfg = Build(bullet: 100, bulletUp: 50);
        Assert.Equal(150, cfg.EnergyForShot(WeaponKind.Bullet, level: 2));
        Assert.Equal(200, cfg.EnergyForShot(WeaponKind.Bullet, level: 3));
    }

    [Fact]
    public void Missing_upgrade_treated_as_zero()
    {
        var cfg = Build(bomb: 500, bombUp: 0);
        Assert.Equal(500, cfg.EnergyForShot(WeaponKind.Bomb, level: 3));
    }

    [Fact]
    public void Multifire_uses_separate_cost_ladder_replacing_bullet_cost()
    {
        // Multifire is a separate projectile: cost = level × MultiFireEnergy, ignoring
        // BulletFireEnergy entirely. With multifire=35:
        //   L1 multifire = 1*35 = 35 (NOT 100+35)
        //   L3 multifire = 3*35 = 105 (NOT 200+35)
        var cfg = Build(bullet: 100, bulletUp: 50, multifire: 35);

        Assert.Equal(35, cfg.EnergyForShot(WeaponKind.Bullet, level: 1, multifire: true));
        Assert.Equal(70, cfg.EnergyForShot(WeaponKind.Bullet, level: 2, multifire: true));
        Assert.Equal(105, cfg.EnergyForShot(WeaponKind.Bullet, level: 3, multifire: true));

        // Single-fire bullet path is unaffected.
        Assert.Equal(100, cfg.EnergyForShot(WeaponKind.Bullet, level: 1, multifire: false));
        Assert.Equal(150, cfg.EnergyForShot(WeaponKind.Bullet, level: 2, multifire: false));
        Assert.Equal(200, cfg.EnergyForShot(WeaponKind.Bullet, level: 3, multifire: false));
    }

    [Fact]
    public void Upgrade_equal_to_base_yields_level_times_base()
    {
        // Adapter substitutes upgrade=base when the upgrade key is missing — that collapses
        // to level*base, which is the empirically correct Continuum behavior.
        var cfg = Build(bullet: 100, bulletUp: 100);
        Assert.Equal(100, cfg.EnergyForShot(WeaponKind.Bullet, level: 1));
        Assert.Equal(200, cfg.EnergyForShot(WeaponKind.Bullet, level: 2));
        Assert.Equal(300, cfg.EnergyForShot(WeaponKind.Bullet, level: 3));
    }

    [Fact]
    public void Unknown_weapon_returns_zero()
    {
        var cfg = Build(bullet: 100);
        Assert.Equal(0, cfg.EnergyForShot(WeaponKind.Thor, level: 1));
        Assert.Equal(0, cfg.EnergyForShot(WeaponKind.BouncingBullet, level: 1));
    }

    [Fact]
    public void Mines_use_separate_costs_from_bombs()
    {
        var cfg = Build(bomb: 500, bombUp: 100, mine: 1000, mineUp: 200);
        Assert.Equal(600, cfg.EnergyForShot(WeaponKind.Bomb, level: 2));
        Assert.Equal(1200, cfg.EnergyForShot(WeaponKind.Mine, level: 2));
    }

    [Fact]
    public void Level_must_be_at_least_one()
    {
        var cfg = Build(bullet: 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => cfg.EnergyForShot(WeaponKind.Bullet, level: 0));
    }

    [Fact]
    public void Negative_costs_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WeaponEnergyConfig(
            new Dictionary<WeaponKind, (int, int)> { [WeaponKind.Bullet] = (-1, 0) },
            multifireBulletEnergy: 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => new WeaponEnergyConfig(
            new Dictionary<WeaponKind, (int, int)> { [WeaponKind.Bullet] = (0, -1) },
            multifireBulletEnergy: 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => new WeaponEnergyConfig(
            new Dictionary<WeaponKind, (int, int)>(),
            multifireBulletEnergy: -1));
    }
}
