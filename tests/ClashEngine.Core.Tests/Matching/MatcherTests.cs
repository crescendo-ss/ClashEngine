using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests.Matching;

public class MatcherTests
{
    private static PlayerKey K(string name) => new(name);
    private static Rating R(double mu) => new(mu, 0.0, 0, default);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public QueueRegistry Registry { get; } = new();
        public MultiQueueIndex Index { get; } = new();
        public TeamBalancer Balancer { get; } = new();
        public IMatchQualityFunction Quality { get; } = new OrdinalSpreadQuality(normalizer: 50.0);
        public Matcher Matcher { get; }

        public Harness()
        {
            Matcher = new Matcher(Registry, Index, Balancer, Quality, Clock);
        }

        public QueueDefinition Register(string name, int teamCount = 2, int perTeam = 2,
            double qStart = 0.5, double qFloor = 0.15, int relaxSeconds = 90, double? maxMuSpread = null) =>
            Registry.Register(
                name,
                new MatchShape(teamCount, perTeam, maxMuSpread: maxMuSpread),
                new PartitionQualityPolicy(qStart, qFloor, TimeSpan.FromSeconds(relaxSeconds)));
    }

    [Fact]
    public void Enqueue_into_unknown_queue_returns_false()
    {
        var h = new Harness();
        Assert.False(h.Matcher.Enqueue(K("Alice"), Rating.Default, "ghost"));
    }

    [Fact]
    public void Enqueue_into_known_queue_succeeds_and_indexes()
    {
        var h = new Harness();
        h.Register("4v4");

        Assert.True(h.Matcher.Enqueue(K("Alice"), Rating.Default, "4v4"));
        Assert.True(h.Index.Contains(K("Alice"), "4v4"));
    }

    [Fact]
    public void Enqueue_duplicate_returns_false()
    {
        var h = new Harness();
        h.Register("4v4");
        h.Matcher.Enqueue(K("Alice"), Rating.Default, "4v4");
        Assert.False(h.Matcher.Enqueue(K("alice"), Rating.Default, "4v4"));
    }

    [Fact]
    public void TryPropose_returns_null_when_not_enough_candidates()
    {
        var h = new Harness();
        h.Register("2v2", teamCount: 2, perTeam: 2);

        h.Matcher.Enqueue(K("Alice"), R(25), "2v2");
        h.Matcher.Enqueue(K("Bob"), R(25), "2v2");

        Assert.Null(h.Matcher.TryProposeMatch());
    }

    [Fact]
    public void TryPropose_with_balanced_pool_succeeds_immediately()
    {
        var h = new Harness();
        h.Register("2v2", qStart: 0.5);

        h.Matcher.Enqueue(K("A"), R(25), "2v2");
        h.Matcher.Enqueue(K("B"), R(25), "2v2");
        h.Matcher.Enqueue(K("C"), R(25), "2v2");
        h.Matcher.Enqueue(K("D"), R(25), "2v2");

        var proposal = h.Matcher.TryProposeMatch();

        Assert.NotNull(proposal);
        Assert.Equal("2v2", proposal!.QueueName);
        Assert.Equal(1.0, proposal.Quality, precision: 6);
        Assert.Equal(4, proposal.Teams.Sum(t => t.Count));
    }

    [Fact]
    public void TryPropose_removes_matched_players_from_their_queue()
    {
        var h = new Harness();
        var def = h.Register("2v2");

        h.Matcher.Enqueue(K("A"), R(25), "2v2");
        h.Matcher.Enqueue(K("B"), R(25), "2v2");
        h.Matcher.Enqueue(K("C"), R(25), "2v2");
        h.Matcher.Enqueue(K("D"), R(25), "2v2");

        h.Matcher.TryProposeMatch();

        Assert.Equal(0, def.Queue.Count);
        Assert.False(h.Index.IsTrackingAnywhere(K("A")));
    }

    [Fact]
    public void Skewed_pool_below_threshold_blocks_until_wait_relaxes()
    {
        // 4 players for a 2v2: 50, 0, 0, 0 (sigma 0).
        // Best partition is {50,0} vs {0,0} → mean ord 25 vs 0 → imbalance 25 → quality = 1 - 25/50 = 0.5.
        var h = new Harness();
        h.Register("2v2", qStart: 0.8, qFloor: 0.1, relaxSeconds: 60);

        h.Matcher.Enqueue(K("A"), R(50), "2v2");
        h.Matcher.Enqueue(K("B"), R(0), "2v2");
        h.Matcher.Enqueue(K("C"), R(0), "2v2");
        h.Matcher.Enqueue(K("D"), R(0), "2v2");

        // best partition = {50,0} vs {0,0} → imbalance 25 → quality = 1 - 25/50 = 0.5
        Assert.Null(h.Matcher.TryProposeMatch());

        // Advance time: at t=60s threshold = qFloor = 0.1 → quality 0.5 > 0.1 → match accepted.
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        var proposal = h.Matcher.TryProposeMatch();
        Assert.NotNull(proposal);
        Assert.Equal(0.5, proposal!.Quality, precision: 6);
    }

    [Fact]
    public void MaxMuSpread_is_forgone_after_the_relax_window_lets_a_match_form()
    {
        // 2v2 with a 15-point MaxMuSpread. Four waiting (sigma 0 -> ordinal = mu): 40,38,36,10.
        // The mu=10 player is 30 below the top, so the roster is ineligible and no match can form
        // while the cap holds. Past RelaxTime the cap is forgone and the best split ({40,10} vs
        // {38,36}, imbalance 12 -> quality 1 - 12/50 = 0.76) clears the floor and pops.
        var h = new Harness();
        h.Register("2v2", qStart: 0.5, qFloor: 0.15, relaxSeconds: 60, maxMuSpread: 15);

        h.Matcher.Enqueue(K("A"), R(40), "2v2");
        h.Matcher.Enqueue(K("B"), R(38), "2v2");
        h.Matcher.Enqueue(K("C"), R(36), "2v2");
        h.Matcher.Enqueue(K("D"), R(10), "2v2");

        Assert.Null(h.Matcher.TryProposeMatch());          // t=0: cap excludes D, roster ineligible

        h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(h.Matcher.TryProposeMatch());          // t=30: still within the relax window

        h.Clock.Advance(TimeSpan.FromSeconds(30));          // t=60: wait >= RelaxTime -> cap forgone
        var proposal = h.Matcher.TryProposeMatch();
        Assert.NotNull(proposal);
        Assert.Equal(0.76, proposal!.Quality, precision: 6);
        Assert.Equal(4, proposal.Teams.Sum(t => t.Count));
    }

    [Fact]
    public void MaxMuSpread_relaxation_still_honors_the_quality_floor()
    {
        // Same relaxation, but here the only achievable match is genuinely lopsided: 50,0,0,0 ->
        // best split {50,0} vs {0,0} scores 0.5, below the 0.6 floor. Forgoing MaxMuSpread makes the
        // roster eligible, but the quality floor is a separate backstop, so no match forms even
        // after the relax window -- relaxing the cap is not the same as accepting any match.
        var h = new Harness();
        h.Register("2v2", qStart: 0.8, qFloor: 0.6, relaxSeconds: 60, maxMuSpread: 15);

        h.Matcher.Enqueue(K("A"), R(50), "2v2");
        h.Matcher.Enqueue(K("B"), R(0), "2v2");
        h.Matcher.Enqueue(K("C"), R(0), "2v2");
        h.Matcher.Enqueue(K("D"), R(0), "2v2");

        Assert.Null(h.Matcher.TryProposeMatch());

        h.Clock.Advance(TimeSpan.FromSeconds(90));          // well past RelaxTime
        Assert.Null(h.Matcher.TryProposeMatch());           // cap forgone, but 0.5 < 0.6 floor
    }

    [Fact]
    public void Multi_queue_search_pop_removes_player_from_all_queues()
    {
        var h = new Harness();
        h.Register("2v2");
        h.Register("4v4", teamCount: 2, perTeam: 4);

        h.Matcher.Enqueue(K("A"), R(25), "2v2");
        h.Matcher.Enqueue(K("A"), R(25), "4v4");
        h.Matcher.Enqueue(K("B"), R(25), "2v2");
        h.Matcher.Enqueue(K("C"), R(25), "2v2");
        h.Matcher.Enqueue(K("D"), R(25), "2v2");

        // 2v2 has 4 players → match. A is matched.
        var proposal = h.Matcher.TryProposeMatch();
        Assert.NotNull(proposal);

        // A should no longer be in 4v4 either.
        Assert.False(h.Registry.TryGet("4v4", out var def4) ? def4.Queue.Contains(K("A")) : true);
        Assert.False(h.Index.IsTrackingAnywhere(K("A")));
    }

    [Fact]
    public void Dequeue_removes_from_specified_queue_only()
    {
        var h = new Harness();
        h.Register("2v2");
        h.Register("4v4", perTeam: 4);

        h.Matcher.Enqueue(K("Alice"), R(25), "2v2");
        h.Matcher.Enqueue(K("Alice"), R(25), "4v4");

        Assert.True(h.Matcher.Dequeue(K("Alice"), "2v2"));
        Assert.False(h.Index.Contains(K("Alice"), "2v2"));
        Assert.True(h.Index.Contains(K("Alice"), "4v4"));
    }

    [Fact]
    public void DequeueEverywhere_removes_player_from_every_queue()
    {
        var h = new Harness();
        h.Register("2v2");
        h.Register("4v4", perTeam: 4);
        h.Register("duel", teamCount: 2, perTeam: 1);

        h.Matcher.Enqueue(K("Alice"), R(25), "2v2");
        h.Matcher.Enqueue(K("Alice"), R(25), "4v4");
        h.Matcher.Enqueue(K("Alice"), R(25), "duel");

        var removed = h.Matcher.DequeueEverywhere(K("Alice"));

        Assert.Equal(3, removed.Count);
        Assert.False(h.Index.IsTrackingAnywhere(K("Alice")));
        foreach (var name in new[] { "2v2", "4v4", "duel" })
        {
            h.Registry.TryGet(name, out var def);
            Assert.False(def!.Queue.Contains(K("Alice")));
        }
    }

    [Fact]
    public void TryPropose_emits_zero_proposals_after_first_when_pool_drained()
    {
        var h = new Harness();
        h.Register("2v2");

        h.Matcher.Enqueue(K("A"), R(25), "2v2");
        h.Matcher.Enqueue(K("B"), R(25), "2v2");
        h.Matcher.Enqueue(K("C"), R(25), "2v2");
        h.Matcher.Enqueue(K("D"), R(25), "2v2");

        Assert.NotNull(h.Matcher.TryProposeMatch());
        Assert.Null(h.Matcher.TryProposeMatch());
    }

    [Fact]
    public void TryPropose_can_emit_back_to_back_for_two_full_pools()
    {
        var h = new Harness();
        h.Register("2v2");

        for (int i = 0; i < 8; i++)
            h.Matcher.Enqueue(K($"P{i}"), R(25), "2v2");

        Assert.NotNull(h.Matcher.TryProposeMatch());
        Assert.NotNull(h.Matcher.TryProposeMatch());
        Assert.Null(h.Matcher.TryProposeMatch());
    }

    [Fact]
    public void Constructor_rejects_null_arguments()
    {
        var registry = new QueueRegistry();
        var index = new MultiQueueIndex();
        var balancer = new TeamBalancer();
        var quality = new OrdinalSpreadQuality();
        var clock = new FakeClock();

        Assert.Throws<ArgumentNullException>(() => new Matcher(null!, index, balancer, quality, clock));
        Assert.Throws<ArgumentNullException>(() => new Matcher(registry, null!, balancer, quality, clock));
        Assert.Throws<ArgumentNullException>(() => new Matcher(registry, index, null!, quality, clock));
        Assert.Throws<ArgumentNullException>(() => new Matcher(registry, index, balancer, null!, clock));
        Assert.Throws<ArgumentNullException>(() => new Matcher(registry, index, balancer, quality, null!));
    }
}
