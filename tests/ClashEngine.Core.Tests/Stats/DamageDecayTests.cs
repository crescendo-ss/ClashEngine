using ClashEngine.Core.Stats;

namespace ClashEngine.Core.Tests.Stats;

public class DamageDecayTests
{
    [Fact]
    public void WeightAt_returns_one_for_current_tick_damage()
    {
        var d = new DamageDecay(halfLifeTicks: 200);
        Assert.Equal(1.0, d.WeightAt(entryTick: 1000, currentTick: 1000), precision: 12);
    }

    [Fact]
    public void WeightAt_clamps_to_one_for_future_entries()
    {
        var d = new DamageDecay(halfLifeTicks: 200);
        Assert.Equal(1.0, d.WeightAt(entryTick: 1500, currentTick: 1000), precision: 12);
    }

    [Fact]
    public void WeightAt_one_half_life_old_is_one_half()
    {
        var d = new DamageDecay(halfLifeTicks: 200);
        Assert.Equal(0.5, d.WeightAt(entryTick: 800, currentTick: 1000), precision: 12);
    }

    [Fact]
    public void WeightAt_two_half_lives_old_is_one_quarter()
    {
        var d = new DamageDecay(halfLifeTicks: 200);
        Assert.Equal(0.25, d.WeightAt(entryTick: 600, currentTick: 1000), precision: 12);
    }

    [Fact]
    public void WeightAt_decays_monotonically()
    {
        var d = new DamageDecay(halfLifeTicks: 200);
        double prev = double.PositiveInfinity;
        for (uint dt = 0; dt <= 2000; dt += 50)
        {
            double w = d.WeightAt(entryTick: 0, currentTick: dt);
            Assert.True(w <= prev, $"weight increased at dt={dt}: {w} > {prev}");
            prev = w;
        }
    }

    [Fact]
    public void Default_half_life_matches_constant()
    {
        var d = new DamageDecay();
        Assert.Equal(DamageDecay.DefaultHalfLifeTicks, d.HalfLifeTicks);
    }

    [Fact]
    public void Half_life_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DamageDecay(halfLifeTicks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DamageDecay(halfLifeTicks: -1));
    }
}
