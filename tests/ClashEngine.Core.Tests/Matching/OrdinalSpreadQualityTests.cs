using ClashEngine.Core.Matching;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Tests.Matching;

public class OrdinalSpreadQualityTests
{
    private static Rating R(double mu, double sigma = 1.0) => new(mu, sigma, 0, default);

    [Fact]
    public void Identical_team_means_yield_quality_one()
    {
        var q = new OrdinalSpreadQuality(normalizer: 50.0);
        var teams = new IReadOnlyList<Rating>[]
        {
            new[] { R(20), R(30) },  // mean ord = (17+27)/2 = 22
            new[] { R(25), R(25) },  // mean ord = (22+22)/2 = 22
        };
        Assert.Equal(1.0, q.Compute(teams), precision: 6);
    }

    [Fact]
    public void Spread_equal_to_normalizer_yields_quality_zero()
    {
        var q = new OrdinalSpreadQuality(normalizer: 10.0);
        var teams = new IReadOnlyList<Rating>[]
        {
            new[] { R(30, 0) },  // ord = 30
            new[] { R(20, 0) },  // ord = 20
        };
        Assert.Equal(0.0, q.Compute(teams), precision: 6);
    }

    [Fact]
    public void Spread_greater_than_normalizer_clamps_to_zero()
    {
        var q = new OrdinalSpreadQuality(normalizer: 5.0);
        var teams = new IReadOnlyList<Rating>[]
        {
            new[] { R(30, 0) },
            new[] { R(10, 0) },
        };
        Assert.Equal(0.0, q.Compute(teams), precision: 6);
    }

    [Fact]
    public void Quality_is_in_zero_to_one_range_for_arbitrary_inputs()
    {
        var q = new OrdinalSpreadQuality();
        var rng = new Random(42);
        for (int i = 0; i < 50; i++)
        {
            var teams = new IReadOnlyList<Rating>[]
            {
                new[] { R(rng.NextDouble() * 50), R(rng.NextDouble() * 50) },
                new[] { R(rng.NextDouble() * 50), R(rng.NextDouble() * 50) },
            };
            double v = q.Compute(teams);
            Assert.InRange(v, 0.0, 1.0);
        }
    }

    [Fact]
    public void Empty_teams_collection_returns_zero()
    {
        var q = new OrdinalSpreadQuality();
        Assert.Equal(0.0, q.Compute(Array.Empty<IReadOnlyList<Rating>>()), precision: 6);
    }

    [Fact]
    public void Non_positive_normalizer_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrdinalSpreadQuality(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrdinalSpreadQuality(-1));
    }
}
