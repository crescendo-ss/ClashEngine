using ClashEngine.Core.Identity;
using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class DamageAttributionTests
{
    private static PlayerKey K(string n) => new(n);

    private static RecentDamage R(uint tick, double amount, string attacker) =>
        new(tick, amount, WeaponKind.Bullet, K(attacker));

    [Fact]
    public void Empty_input_yields_empty_dictionary()
    {
        var d = new DamageDecay();
        var result = DamageAttribution.AttributeRawAndWeighted(Array.Empty<RecentDamage>(), currentTick: 1000, d);
        Assert.Empty(result);
    }

    [Fact]
    public void Single_entry_at_current_tick_yields_full_amount()
    {
        var d = new DamageDecay(halfLifeTicks: 200);
        var entries = new[] { R(1000, 80, "A") };
        var result = DamageAttribution.AttributeRawAndWeighted(entries, currentTick: 1000, d);
        Assert.Equal(80.0, result[K("A")].Raw, precision: 9);
        Assert.Equal(80.0, result[K("A")].Weighted, precision: 9);
    }

    [Fact]
    public void Single_entry_one_half_life_old_yields_half_weighted_full_raw()
    {
        var d = new DamageDecay(halfLifeTicks: 200);
        var entries = new[] { R(800, 100, "A") };
        var result = DamageAttribution.AttributeRawAndWeighted(entries, currentTick: 1000, d);
        Assert.Equal(100.0, result[K("A")].Raw, precision: 9);
        Assert.Equal(50.0, result[K("A")].Weighted, precision: 9);
    }

    [Fact]
    public void Multiple_entries_same_attacker_sum()
    {
        var d = new DamageDecay(halfLifeTicks: 200);
        var entries = new[]
        {
            R(1000, 30, "A"), // weight 1.0  → weighted 30, raw 30
            R(800,  40, "A"), // weight 0.5  → weighted 20, raw 40
            R(600,  60, "A"), // weight 0.25 → weighted 15, raw 60
        };
        var result = DamageAttribution.AttributeRawAndWeighted(entries, currentTick: 1000, d);
        Assert.Equal(130.0, result[K("A")].Raw, precision: 9);
        Assert.Equal(65.0, result[K("A")].Weighted, precision: 9);
    }

    [Fact]
    public void Multiple_attackers_partitioned_separately()
    {
        var d = new DamageDecay(halfLifeTicks: 200);
        var entries = new[]
        {
            R(1000, 100, "A"),
            R(1000,  50, "B"),
            R(800,   40, "A"), // weight 0.5 → weighted 20, raw 40
        };
        var result = DamageAttribution.AttributeRawAndWeighted(entries, currentTick: 1000, d);
        Assert.Equal(140.0, result[K("A")].Raw, precision: 9);
        Assert.Equal(120.0, result[K("A")].Weighted, precision: 9);
        Assert.Equal(50.0, result[K("B")].Raw, precision: 9);
        Assert.Equal(50.0, result[K("B")].Weighted, precision: 9);
    }

    [Fact]
    public void Non_positive_amounts_are_ignored()
    {
        var d = new DamageDecay();
        var entries = new[]
        {
            R(1000, 50, "A"),
            R(1000, 0, "B"),
            R(1000, -10, "C"),
        };
        var result = DamageAttribution.AttributeRawAndWeighted(entries, currentTick: 1000, d);
        Assert.Single(result);
        Assert.Contains(K("A"), result.Keys);
    }
}
