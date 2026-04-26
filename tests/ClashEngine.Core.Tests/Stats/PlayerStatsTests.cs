using ClashEngine.Core.Identity;
using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class PlayerStatsTests
{
    private static PlayerKey K(string n) => new(n);

    private static PlayerStats Make() =>
        new(K("X"), new DamageRecoveryState(rechargeRate: 1.0));

    [Fact]
    public void New_player_has_zero_counters_and_no_lives()
    {
        var p = Make();
        Assert.Equal(0, p.Kills);
        Assert.Equal(0, p.Deaths);
        Assert.Empty(p.Lives);
        Assert.Null(p.CurrentLife);
        Assert.Empty(p.PerWeapon);
        Assert.Empty(p.ItemUses);
        Assert.Empty(p.WastedItems);
        Assert.Empty(p.DistanceSamples);
    }

    [Fact]
    public void Open_life_creates_and_returns_open_life()
    {
        var p = Make();
        var life = p.OpenLife(100);
        Assert.Same(life, p.CurrentLife);
        Assert.Equal(100u, life.StartTick);
        Assert.False(life.IsClosed);
    }

    [Fact]
    public void Open_life_is_idempotent_when_one_is_already_open()
    {
        var p = Make();
        var first = p.OpenLife(100);
        var second = p.OpenLife(200);  // should not open a second open life
        Assert.Same(first, second);
        Assert.Single(p.Lives);
    }

    [Fact]
    public void Close_life_then_open_starts_a_new_one()
    {
        var p = Make();
        p.OpenLife(100);
        p.CloseLife(150, LifeEndReason.KilledByEnemy, knockoutBy: K("Y"));
        Assert.Null(p.CurrentLife);
        var second = p.OpenLife(160);
        Assert.Equal(160u, second.StartTick);
        Assert.Equal(2, p.Lives.Count);
    }

    [Fact]
    public void Damage_dealt_accumulates_to_current_life()
    {
        var p = Make();
        p.OpenLife(100);
        p.AddDamageDealt(50);
        p.AddDamageDealt(30);
        Assert.Equal(80, p.DamageDealt);
        Assert.Equal(80, p.CurrentLife!.DamageDealt);
    }

    [Fact]
    public void Damage_dealt_outside_a_life_only_updates_match_total()
    {
        var p = Make();
        p.AddDamageDealt(50);
        Assert.Equal(50, p.DamageDealt);
        Assert.Null(p.CurrentLife);
    }

    [Fact]
    public void Per_weapon_get_or_create_returns_same_instance()
    {
        var p = Make();
        var a = p.GetOrCreateWeapon(WeaponKind.Bullet);
        var b = p.GetOrCreateWeapon(WeaponKind.Bullet);
        Assert.Same(a, b);
        a.RecordShot(energy: 100);
        a.RecordHit();
        Assert.Equal(1, b.FireCount);
        Assert.Equal(1, b.HitCount);
        Assert.Equal(100, b.EnergySpent);
    }

    [Fact]
    public void Item_use_increments_per_kind()
    {
        var p = Make();
        p.RecordItemUse(ItemKind.Repel);
        p.RecordItemUse(ItemKind.Repel);
        p.RecordItemUse(ItemKind.Burst);
        Assert.Equal(2, p.ItemUses[ItemKind.Repel]);
        Assert.Equal(1, p.ItemUses[ItemKind.Burst]);
    }

    [Fact]
    public void Record_kill_increments_match_and_life_counters()
    {
        var p = Make();
        p.OpenLife(100);
        p.RecordKill();
        Assert.Equal(1, p.Kills);
        Assert.Equal(1, p.CurrentLife!.Kills);
    }

    [Fact]
    public void Constructor_rejects_default_player_key()
    {
        Assert.Throws<ArgumentException>(() =>
            new PlayerStats(default, new DamageRecoveryState(rechargeRate: 1.0)));
    }
}
