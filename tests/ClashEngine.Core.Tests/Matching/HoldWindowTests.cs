using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests.Matching;

public class HoldWindowTests
{
    private static PlayerKey K(string n) => new(n);
    private static Rating R(double mu) => new(mu, 0, 0, default);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public QueueRegistry Registry { get; } = new();
        public MultiQueueIndex Index { get; } = new();
        public TeamBalancer Balancer { get; } = new();
        public IMatchQualityFunction Quality { get; } = new OrdinalSpreadQuality(50.0);
        public Matcher Matcher { get; }

        public Harness(int lookAhead, TimeSpan holdWindow, double qualityCeiling)
        {
            Matcher = new Matcher(Registry, Index, Balancer, Quality, Clock);
            Registry.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.4, 0.1, TimeSpan.FromSeconds(60)),
                "gt1",
                lookAheadWindow: lookAhead,
                holdWindow: holdWindow,
                qualityCeiling: qualityCeiling);
        }
    }

    [Fact]
    public void Pool_at_lookahead_max_pops_without_hold()
    {
        // poolSize == lookAheadWindow → no headroom → pop immediately even if below ceiling.
        var h = new Harness(lookAhead: 4, holdWindow: TimeSpan.FromSeconds(10), qualityCeiling: 0.99);
        h.Matcher.Enqueue(K("A"), R(50), "2v2");
        h.Matcher.Enqueue(K("B"), R(0), "2v2");
        h.Matcher.Enqueue(K("C"), R(0), "2v2");
        h.Matcher.Enqueue(K("D"), R(0), "2v2");

        var proposal = h.Matcher.TryProposeMatch();
        Assert.NotNull(proposal);
    }

    [Fact]
    public void Quality_at_ceiling_pops_immediately_even_with_headroom()
    {
        // Perfect-balance pool. Ceiling 0.9 → first tick pops.
        var h = new Harness(lookAhead: 6, holdWindow: TimeSpan.FromSeconds(10), qualityCeiling: 0.9);
        h.Matcher.Enqueue(K("A"), R(25), "2v2");
        h.Matcher.Enqueue(K("B"), R(25), "2v2");
        h.Matcher.Enqueue(K("C"), R(25), "2v2");
        h.Matcher.Enqueue(K("D"), R(25), "2v2");

        Assert.NotNull(h.Matcher.TryProposeMatch());
    }

    [Fact]
    public void Sub_ceiling_match_with_lookahead_headroom_holds_until_window_expires()
    {
        // Pool 4 of 6 lookahead. Skewed ratings → quality < 0.9 ceiling. Hold 10s, no new arrivals.
        var h = new Harness(lookAhead: 6, holdWindow: TimeSpan.FromSeconds(10), qualityCeiling: 0.9);
        h.Matcher.Enqueue(K("A"), R(50), "2v2");
        h.Matcher.Enqueue(K("B"), R(0), "2v2");
        h.Matcher.Enqueue(K("C"), R(0), "2v2");
        h.Matcher.Enqueue(K("D"), R(0), "2v2");

        Assert.Null(h.Matcher.TryProposeMatch());   // first call: held, not popped
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(h.Matcher.TryProposeMatch());   // still inside hold window
        h.Clock.Advance(TimeSpan.FromSeconds(6));   // 11s total
        Assert.NotNull(h.Matcher.TryProposeMatch()); // hold window elapsed → pop
    }

    [Fact]
    public void Better_arrivals_during_hold_improve_the_chosen_partition()
    {
        // Initial pool: {50, 0, 0, 0} → best partition {50,0}/{0,0} = mean 25 vs 0 = quality 0.5.
        // Held below ceiling 0.99. New 50 arrives: best 4-subset {50, 50, 0, 0} = quality 1.0 → pops.
        var h = new Harness(lookAhead: 6, holdWindow: TimeSpan.FromSeconds(10), qualityCeiling: 0.99);
        h.Matcher.Enqueue(K("A"), R(50), "2v2");
        h.Matcher.Enqueue(K("B"), R(0), "2v2");
        h.Matcher.Enqueue(K("C"), R(0), "2v2");
        h.Matcher.Enqueue(K("D"), R(0), "2v2");

        Assert.Null(h.Matcher.TryProposeMatch());   // held — quality 0.5 < 0.99

        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Matcher.Enqueue(K("E"), R(50), "2v2");
        var proposal = h.Matcher.TryProposeMatch();

        Assert.NotNull(proposal);
        Assert.Equal(1.0, proposal!.Quality, precision: 6);   // upgraded match, perfect balance
    }

    [Fact]
    public void Held_candidate_invalidates_when_one_of_its_players_dequeues()
    {
        var h = new Harness(lookAhead: 6, holdWindow: TimeSpan.FromSeconds(10), qualityCeiling: 0.99);
        h.Matcher.Enqueue(K("A"), R(50), "2v2");
        h.Matcher.Enqueue(K("B"), R(0), "2v2");
        h.Matcher.Enqueue(K("C"), R(0), "2v2");
        h.Matcher.Enqueue(K("D"), R(0), "2v2");

        Assert.Null(h.Matcher.TryProposeMatch());   // hold begins

        h.Matcher.Dequeue(K("A"), "2v2");           // longest waiter leaves
        // Now only 3 candidates left → no proposal possible. The previously held candidate
        // should be discarded; we should not pop a stale match for A.
        h.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Null(h.Matcher.TryProposeMatch());
    }
}
