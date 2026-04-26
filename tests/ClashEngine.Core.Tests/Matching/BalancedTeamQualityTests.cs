using ClashEngine.Core.Matching;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Tests.Matching;

public class BalancedTeamQualityTests
{
    private static Rating R(double mu, double sigma = 0) => new(mu, sigma, 0, default);

    [Fact]
    public void Identical_players_yield_quality_one()
    {
        var q = new BalancedTeamQuality();
        var teams = new IReadOnlyList<Rating>[]
        {
            new[] { R(25), R(25), R(25), R(25) },
            new[] { R(25), R(25), R(25), R(25) },
        };
        Assert.Equal(1.0, q.Compute(teams), precision: 6);
    }

    [Fact]
    public void Equal_means_but_high_intra_team_variance_is_penalized()
    {
        // Both teams have mean 25, but each contains a "liability" 5 paired with a 45.
        var q = new BalancedTeamQuality(
            meanSpreadNormalizer: 50, playerDriftNormalizer: 25,
            meanWeight: 0.5, driftWeight: 0.5);
        var balanced = new IReadOnlyList<Rating>[]
        {
            new[] { R(25), R(25), R(25), R(25) },
            new[] { R(25), R(25), R(25), R(25) },
        };
        var bookended = new IReadOnlyList<Rating>[]
        {
            new[] { R(45), R(35), R(15), R(5) },   // mean 25, drift up to 20
            new[] { R(45), R(35), R(15), R(5) },   // mean 25, drift up to 20
        };

        double qBal = q.Compute(balanced);
        double qBook = q.Compute(bookended);

        Assert.True(qBal > qBook,
            $"balanced ({qBal}) should beat bookended ({qBook}) because of intra-team drift.");
    }

    [Fact]
    public void OrdinalSpreadQuality_does_not_distinguish_balanced_from_bookended()
    {
        // The whole point of BalancedTeamQuality: this case looks identical to OrdinalSpreadQuality.
        var q = new OrdinalSpreadQuality(normalizer: 50);
        var balanced = new IReadOnlyList<Rating>[]
        {
            new[] { R(25), R(25) },
            new[] { R(25), R(25) },
        };
        var bookended = new IReadOnlyList<Rating>[]
        {
            new[] { R(45), R(5) },
            new[] { R(45), R(5) },
        };
        Assert.Equal(q.Compute(balanced), q.Compute(bookended), precision: 6);
    }

    [Fact]
    public void Inter_team_spread_alone_lowers_quality()
    {
        var q = new BalancedTeamQuality();
        var teams = new IReadOnlyList<Rating>[]
        {
            new[] { R(40), R(40) },  // mean 40
            new[] { R(10), R(10) },  // mean 10
        };
        double v = q.Compute(teams);
        Assert.True(v < 1.0);
        Assert.InRange(v, 0.0, 1.0);
    }

    [Fact]
    public void Quality_in_zero_to_one()
    {
        var q = new BalancedTeamQuality();
        var rng = new Random(11);
        for (int i = 0; i < 50; i++)
        {
            var teams = new IReadOnlyList<Rating>[]
            {
                new[] { R(rng.NextDouble() * 50), R(rng.NextDouble() * 50) },
                new[] { R(rng.NextDouble() * 50), R(rng.NextDouble() * 50) },
            };
            Assert.InRange(q.Compute(teams), 0.0, 1.0);
        }
    }

    [Fact]
    public void Setting_drift_weight_to_zero_recovers_OrdinalSpread_behavior()
    {
        var q = new BalancedTeamQuality(meanSpreadNormalizer: 50, driftWeight: 0, meanWeight: 1.0);
        var bookended = new IReadOnlyList<Rating>[]
        {
            new[] { R(45), R(5) },  // mean 25
            new[] { R(45), R(5) },  // mean 25
        };
        Assert.Equal(1.0, q.Compute(bookended), precision: 6);
    }

    [Fact]
    public void Validates_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BalancedTeamQuality(meanSpreadNormalizer: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BalancedTeamQuality(playerDriftNormalizer: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BalancedTeamQuality(meanWeight: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BalancedTeamQuality(driftWeight: -1));
    }
}
