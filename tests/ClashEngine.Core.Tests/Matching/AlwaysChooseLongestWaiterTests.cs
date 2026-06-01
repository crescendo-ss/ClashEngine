using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests.Matching;

/// <summary>
/// Covers <see cref="QueueDefinition.AlwaysChooseLongestWaiter"/> / the balancer's
/// <c>requireLongestWaiter</c> flag. Scenario: a high-rated outlier sits at the head of a 2v2
/// queue with four equal mid-rated players behind. With the head pinned (default) the only matches
/// available are imbalanced; releasing the pin lets the balancer form the perfect 25/25 match that
/// excludes the head.
/// </summary>
public class AlwaysChooseLongestWaiterTests
{
    private static PlayerKey K(string n) => new(n);
    private static Rating R(double mu) => new(mu, 0.0, 0, default);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IMatchQualityFunction Quality = new OrdinalSpreadQuality(50.0);

    private static QueueEntry[] OutlierHeadPool() => new[]
    {
        new QueueEntry(K("A"), R(70), T0),               // head: high outlier
        new QueueEntry(K("B"), R(25), T0.AddSeconds(1)),
        new QueueEntry(K("C"), R(25), T0.AddSeconds(2)),
        new QueueEntry(K("D"), R(25), T0.AddSeconds(3)),
        new QueueEntry(K("E"), R(25), T0.AddSeconds(4)),
    };

    [Fact]
    public void Default_pins_the_longest_waiter_into_the_match()
    {
        var result = new TeamBalancer().FindBest(OutlierHeadPool(), new MatchShape(2, 2), Quality);

        var players = result!.Teams.SelectMany(t => t).ToList();
        Assert.Contains(K("A"), players);
    }

    [Fact]
    public void Disabling_it_lets_the_head_be_passed_over_for_better_balance()
    {
        var result = new TeamBalancer().FindBest(
            OutlierHeadPool(), new MatchShape(2, 2), Quality, requireLongestWaiter: false);

        var players = result!.Teams.SelectMany(t => t).ToList();
        Assert.DoesNotContain(K("A"), players);     // head excluded
        Assert.Equal(1.0, result.Quality, 6);       // perfect 25/25 split
    }

    [Fact]
    public void Matcher_pops_a_head_excluding_match_when_the_toggle_is_off()
    {
        var registry = new QueueRegistry();
        var matcher = new Matcher(registry, new MultiQueueIndex(), new TeamBalancer(), Quality, new FakeClock(T0));
        registry.Register("2v2", new MatchShape(2, 2),
            new PartitionQualityPolicy(0.4, 0.1, TimeSpan.FromSeconds(60)), "gt1",
            lookAheadWindow: 5, holdWindow: TimeSpan.Zero, qualityCeiling: 0.9,
            alwaysChooseLongestWaiter: false);

        matcher.Enqueue(K("A"), R(70), "2v2");
        matcher.Enqueue(K("B"), R(25), "2v2");
        matcher.Enqueue(K("C"), R(25), "2v2");
        matcher.Enqueue(K("D"), R(25), "2v2");
        matcher.Enqueue(K("E"), R(25), "2v2");

        var proposal = matcher.TryProposeMatch();

        Assert.NotNull(proposal);
        var players = proposal!.Teams.SelectMany(t => t).ToList();
        Assert.DoesNotContain(K("A"), players);
        Assert.Equal(1.0, proposal.Quality, 6);
    }
}
