using ClashEngine.Core.Matching;

namespace ClashEngine.Core.Tests.Matching;

public class MatchShapeTests
{
    [Fact]
    public void Total_players_is_team_count_times_per_team()
    {
        Assert.Equal(8, new MatchShape(2, 4).TotalPlayers);
        Assert.Equal(12, new MatchShape(4, 3).TotalPlayers);
    }

    [Fact]
    public void Validates_team_count_is_at_least_two()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchShape(1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchShape(0, 4));
    }

    [Fact]
    public void Validates_players_per_team_is_at_least_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchShape(2, 0));
    }

    [Fact]
    public void Validates_max_ordinal_spread_is_non_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchShape(2, 4, -0.1));
    }

    [Fact]
    public void Equality_is_value_based()
    {
        Assert.Equal(new MatchShape(2, 4), new MatchShape(2, 4));
        Assert.NotEqual(new MatchShape(2, 4), new MatchShape(4, 2));
    }
}
