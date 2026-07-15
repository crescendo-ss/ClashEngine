using ClashEngine.Core.Matching;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Tests.Matching;

public class PredictDrawQualityTests
{
    private static Rating R(double mu, double sigma) => new(mu, sigma, 0, default);

    [Fact]
    public void Identical_teams_yield_quality_one()
    {
        var q = new PredictDrawQuality();
        var teams = new IReadOnlyList<Rating>[]
        {
            new[] { R(30, 5), R(20, 5) },
            new[] { R(30, 5), R(20, 5) },
        };
        // Symmetric matchup -> each side ~50% win -> spread 0 -> quality 1.
        Assert.Equal(1.0, q.Compute(teams), precision: 6);
    }

    [Fact]
    public void Certain_blowout_yields_quality_near_zero()
    {
        var q = new PredictDrawQuality();
        var teams = new IReadOnlyList<Rating>[]
        {
            new[] { R(50, 1), R(50, 1) },
            new[] { R(5, 1), R(5, 1) },
        };
        Assert.InRange(q.Compute(teams), 0.0, 0.05);
    }

    [Fact]
    public void More_uncertainty_reads_as_more_balanced_for_the_same_mu_edge()
    {
        // Same mu edge (Team1 +5 mu per player) under low vs high sigma. OpenSkill is less sure of
        // the outcome when sigma is high, so predict_win sits closer to 50/50 -> higher quality.
        // This is exactly the sigma-awareness that a raw mu-difference criterion misses.
        var q = new PredictDrawQuality();
        var lowSigma = new IReadOnlyList<Rating>[]
        {
            new[] { R(30, 1), R(30, 1) },
            new[] { R(25, 1), R(25, 1) },
        };
        var highSigma = new IReadOnlyList<Rating>[]
        {
            new[] { R(30, 8), R(30, 8) },
            new[] { R(25, 8), R(25, 8) },
        };
        Assert.True(q.Compute(highSigma) > q.Compute(lowSigma),
            "Higher sigma should read as closer to a coin flip for the same mu edge.");
    }

    [Fact]
    public void Quality_is_in_zero_to_one_range_for_arbitrary_inputs()
    {
        var q = new PredictDrawQuality();
        var rng = new Random(42);
        for (int i = 0; i < 50; i++)
        {
            var teams = new IReadOnlyList<Rating>[]
            {
                new[] { R(rng.NextDouble() * 50, 1 + rng.NextDouble() * 7), R(rng.NextDouble() * 50, 1 + rng.NextDouble() * 7) },
                new[] { R(rng.NextDouble() * 50, 1 + rng.NextDouble() * 7), R(rng.NextDouble() * 50, 1 + rng.NextDouble() * 7) },
            };
            Assert.InRange(q.Compute(teams), 0.0, 1.0);
        }
    }

    [Fact]
    public void Empty_or_single_team_returns_zero()
    {
        var q = new PredictDrawQuality();
        Assert.Equal(0.0, q.Compute(Array.Empty<IReadOnlyList<Rating>>()), precision: 6);
        Assert.Equal(0.0, q.Compute(new IReadOnlyList<Rating>[] { new[] { R(25, 8) } }), precision: 6);
    }
}
