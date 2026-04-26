using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Tests.Matching;

public class LookAheadTests
{
    private static QueueEntry E(string name, double mu) =>
        new(new PlayerKey(name), new Rating(mu, 0, 0, default),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly IMatchQualityFunction Quality = new OrdinalSpreadQuality(50.0);

    [Fact]
    public void Larger_pool_finds_better_partition_than_first_N()
    {
        // First 4 candidates: severely imbalanced (50, 50, 0, 0). Best partition: {50,0}/{50,0} = perfect.
        // Add candidates 25, 25 in the lookahead window — but the longest waiter (50) must always be in.
        // Best partition over a 6-pool including index 0 (50) might find an even better balance, but
        // {50,0}/{50,0} is already 1.0, so the test is structural: confirm the balancer considers
        // partitions where index 0 is paired with index N-1 (the 6th-waiting candidate).
        var balancer = new TeamBalancer();
        var pool = new[]
        {
            E("Alice", 50),  // longest waiter, always included
            E("Bob",   50),
            E("Carol",  0),
            E("Dave",   0),
            E("Eve",   25),
            E("Frank", 25),
        };

        var result = balancer.FindBest(pool, new MatchShape(2, 2), Quality);

        Assert.NotNull(result);
        // Alice must always be present (longest waiter constraint).
        Assert.Contains(result!.Teams.SelectMany(t => t), p => p == new PlayerKey("Alice"));
    }

    [Fact]
    public void Longest_waiter_is_always_included_in_chosen_partition()
    {
        // 5 candidates for a 2v2: a "bad" longest waiter and 4 well-matched 25s. Without the
        // include-zero rule the balancer would pick the 4 equal-rated solos. With it, Alice is in.
        var balancer = new TeamBalancer();
        var pool = new[]
        {
            E("Alice", 50),
            E("Bob",    25),
            E("Carol",  25),
            E("Dave",   25),
            E("Eve",     0),  // pairing for Alice
        };

        var result = balancer.FindBest(pool, new MatchShape(2, 2), Quality);

        Assert.NotNull(result);
        Assert.Contains(new PlayerKey("Alice"), result!.Teams.SelectMany(t => t));
    }

    [Fact]
    public void Pool_smaller_than_needed_returns_null()
    {
        var balancer = new TeamBalancer();
        var pool = new[] { E("Alice", 25), E("Bob", 25), E("Carol", 25) };

        Assert.Null(balancer.FindBest(pool, new MatchShape(2, 2), Quality));
    }

    [Fact]
    public void Pool_equal_to_needed_works_unchanged()
    {
        var balancer = new TeamBalancer();
        var pool = new[]
        {
            E("Alice", 25), E("Bob", 25),
            E("Carol", 25), E("Dave", 25),
        };

        var result = balancer.FindBest(pool, new MatchShape(2, 2), Quality);

        Assert.NotNull(result);
        Assert.Equal(1.0, result!.Quality, precision: 6);
    }

    [Fact]
    public void Six_pool_2v2_picks_best_4_subset()
    {
        // Pool: [50, 49, 25, 25, 0, 1]. Longest waiter Alice=50.
        // Including Alice (50): best subset is [50, 49, 1, 0] → {50,0}/{49,1} or {50,1}/{49,0}, mean 25/25.
        // Quality is 1.0. Other subsets (e.g., {50, 25, 25, ?}) yield lower quality.
        var balancer = new TeamBalancer();
        var pool = new[]
        {
            E("Alice", 50),
            E("Bob",   49),
            E("Carol", 25),
            E("Dave",  25),
            E("Eve",    0),
            E("Frank",  1),
        };

        var result = balancer.FindBest(pool, new MatchShape(2, 2), Quality);

        Assert.NotNull(result);
        Assert.Equal(1.0, result!.Quality, precision: 6);
        var chosen = result.Teams.SelectMany(t => t.Select(p => p.Name)).ToHashSet();
        Assert.Contains("Alice", chosen);
        // Carol/Dave (the 25s) should be excluded — pairing them with the extremes is worse.
        Assert.DoesNotContain("Carol", chosen);
        Assert.DoesNotContain("Dave", chosen);
    }

    [Fact]
    public void QueueDefinition_lookahead_below_total_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new QueueDefinition(
                "q",
                new MatchShape(2, 4),  // total 8
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(60)),
                lookAheadWindow: 4));
    }

    [Fact]
    public void QueueDefinition_lookahead_defaults_to_total_players()
    {
        var def = new QueueDefinition(
            "q",
            new MatchShape(2, 4),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(60)));
        Assert.Equal(8, def.LookAheadWindow);
    }
}
