using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Tests.Matching;

public class TeamBalancerGroupTests
{
    private static QueueEntry E(string name, double mu, GroupId? group = null) =>
        new(new PlayerKey(name), new Rating(mu, 0, 0, default),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), group);

    private static readonly IMatchQualityFunction Quality = new OrdinalSpreadQuality(normalizer: 50.0);

    [Fact]
    public void Without_groups_require_groups_together_returns_any_partition()
    {
        var balancer = new TeamBalancer();
        var candidates = new[] { E("A", 25), E("B", 25), E("C", 25), E("D", 25) };

        var result = balancer.FindBest(candidates, new MatchShape(2, 2), Quality, requireGroupsTogether: true);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Teams.Sum(t => t.Count));
    }

    [Fact]
    public void With_a_group_keeps_members_on_same_team_when_required()
    {
        var balancer = new TeamBalancer();
        var g = GroupId.New();
        var candidates = new[]
        {
            E("A", 25, g), E("B", 25, g),  // group of 2
            E("C", 25), E("D", 25),         // solos
        };

        var result = balancer.FindBest(candidates, new MatchShape(2, 2), Quality, requireGroupsTogether: true);

        Assert.NotNull(result);
        var teamWithA = result!.Teams.First(t => t.Any(p => p == new PlayerKey("A")));
        Assert.Contains(new PlayerKey("B"), teamWithA);
    }

    [Fact]
    public void Group_too_large_for_a_team_yields_null_when_required()
    {
        var balancer = new TeamBalancer();
        var g = GroupId.New();
        var candidates = new[]
        {
            E("A", 25, g), E("B", 25, g), E("C", 25, g),  // group of 3 in 2v2 — impossible to keep together
            E("D", 25),
        };

        var result = balancer.FindBest(candidates, new MatchShape(2, 2), Quality, requireGroupsTogether: true);
        Assert.Null(result);
    }

    [Fact]
    public void Group_too_large_falls_back_when_split_is_allowed()
    {
        var balancer = new TeamBalancer();
        var g = GroupId.New();
        var candidates = new[]
        {
            E("A", 25, g), E("B", 25, g), E("C", 25, g),
            E("D", 25),
        };

        var splitResult = balancer.FindBest(candidates, new MatchShape(2, 2), Quality, requireGroupsTogether: false);
        Assert.NotNull(splitResult);
    }

    [Fact]
    public void Two_groups_of_two_in_a_2v2_each_team_has_one_group()
    {
        var balancer = new TeamBalancer();
        var g1 = GroupId.New();
        var g2 = GroupId.New();
        var candidates = new[]
        {
            E("A", 25, g1), E("B", 25, g1),
            E("C", 25, g2), E("D", 25, g2),
        };

        var result = balancer.FindBest(candidates, new MatchShape(2, 2), Quality, requireGroupsTogether: true);

        Assert.NotNull(result);
        var teamWithA = result!.Teams.First(t => t.Any(p => p == new PlayerKey("A")));
        Assert.Contains(new PlayerKey("B"), teamWithA);
        Assert.DoesNotContain(new PlayerKey("C"), teamWithA);
        Assert.DoesNotContain(new PlayerKey("D"), teamWithA);
    }

    [Fact]
    public void Solo_players_can_fill_around_groups()
    {
        var balancer = new TeamBalancer();
        var g = GroupId.New();
        // 4v4: a duo group + 6 solos.
        var candidates = new[]
        {
            E("A", 25, g), E("B", 25, g),
            E("C", 25), E("D", 25), E("E", 25), E("F", 25), E("G", 25), E("H", 25),
        };

        var result = balancer.FindBest(candidates, new MatchShape(2, 4), Quality, requireGroupsTogether: true);

        Assert.NotNull(result);
        var teamWithA = result!.Teams.First(t => t.Any(p => p == new PlayerKey("A")));
        Assert.Contains(new PlayerKey("B"), teamWithA);
    }
}
