using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Tests.Ratings;

public class RatingTests
{
    [Fact]
    public void Default_has_OpenSkill_starting_values()
    {
        var r = Rating.Default;
        Assert.Equal(25.0, r.Mu, precision: 6);
        Assert.Equal(25.0 / 3.0, r.Sigma, precision: 6);
        Assert.Equal(0u, r.GamesPlayed);
    }

    [Fact]
    public void Ordinal_is_mu_minus_3_sigma()
    {
        var r = new Rating(30.0, 5.0, 10, DateTimeOffset.UtcNow);
        Assert.Equal(15.0, r.Ordinal, precision: 6);
    }

    [Fact]
    public void Default_ordinal_is_zero()
    {
        Assert.Equal(0.0, Rating.Default.Ordinal, precision: 6);
    }

    [Fact]
    public void WithGameRecorded_increments_games_and_updates_fields()
    {
        var r = new Rating(25.0, 8.0, 4, DateTimeOffset.MinValue);
        var when = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var updated = r.WithGameRecorded(28.0, 7.0, when);

        Assert.Equal(28.0, updated.Mu);
        Assert.Equal(7.0, updated.Sigma);
        Assert.Equal(5u, updated.GamesPlayed);
        Assert.Equal(when, updated.LastSeen);
    }

    [Fact]
    public void Equality_is_value_based()
    {
        var when = DateTimeOffset.UtcNow;
        Assert.Equal(new Rating(25, 8.33, 0, when), new Rating(25, 8.33, 0, when));
    }
}
