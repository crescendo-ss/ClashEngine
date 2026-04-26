using ClashEngine.Core.Matching;

namespace ClashEngine.Core.Tests.Matching;

public class PartitionQualityPolicyTests
{
    [Fact]
    public void Default_starts_at_QStart_for_zero_wait()
    {
        var p = PartitionQualityPolicy.Default;
        Assert.Equal(p.QStart, p.MinQuality(TimeSpan.Zero), precision: 6);
    }

    [Fact]
    public void Default_returns_QFloor_after_RelaxTime()
    {
        var p = PartitionQualityPolicy.Default;
        Assert.Equal(p.QFloor, p.MinQuality(p.RelaxTime), precision: 6);
        Assert.Equal(p.QFloor, p.MinQuality(p.RelaxTime + TimeSpan.FromSeconds(60)), precision: 6);
    }

    [Fact]
    public void Mid_wait_is_halfway_between_start_and_floor()
    {
        var p = new PartitionQualityPolicy(qStart: 0.6, qFloor: 0.2, relaxTime: TimeSpan.FromSeconds(100));
        var halfway = p.MinQuality(TimeSpan.FromSeconds(50));
        Assert.Equal(0.4, halfway, precision: 6);
    }

    [Fact]
    public void Decay_is_strictly_monotonic_until_floor_reached()
    {
        var p = new PartitionQualityPolicy(qStart: 0.5, qFloor: 0.15, relaxTime: TimeSpan.FromSeconds(90));
        double prev = double.PositiveInfinity;
        for (int s = 0; s <= 90; s += 5)
        {
            double q = p.MinQuality(TimeSpan.FromSeconds(s));
            Assert.True(q <= prev, $"non-monotonic at s={s}: {q} > {prev}");
            prev = q;
        }
    }

    [Fact]
    public void Negative_wait_is_treated_as_zero()
    {
        var p = PartitionQualityPolicy.Default;
        Assert.Equal(p.QStart, p.MinQuality(TimeSpan.FromSeconds(-10)), precision: 6);
    }

    [Fact]
    public void Custom_floor_above_zero_is_respected()
    {
        var p = new PartitionQualityPolicy(qStart: 0.8, qFloor: 0.5, relaxTime: TimeSpan.FromMinutes(2));
        Assert.Equal(0.5, p.MinQuality(TimeSpan.FromMinutes(5)), precision: 6);
    }

    [Theory]
    [InlineData(-0.1, 0.5, 60)]
    [InlineData(1.1, 0.5, 60)]
    [InlineData(0.5, -0.1, 60)]
    [InlineData(0.5, 1.1, 60)]
    public void Out_of_range_quality_throws(double start, double floor, double relaxSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PartitionQualityPolicy(start, floor, TimeSpan.FromSeconds(relaxSeconds)));
    }

    [Fact]
    public void Floor_greater_than_start_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new PartitionQualityPolicy(qStart: 0.2, qFloor: 0.5, relaxTime: TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Zero_or_negative_relax_time_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PartitionQualityPolicy(0.5, 0.2, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PartitionQualityPolicy(0.5, 0.2, TimeSpan.FromSeconds(-1)));
    }
}
