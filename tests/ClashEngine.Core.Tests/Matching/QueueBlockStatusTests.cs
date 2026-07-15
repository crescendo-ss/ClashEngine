using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests.Matching;

/// <summary>
/// Covers the per-queue "why isn't this full queue starting?" diagnostic the matcher caches and
/// emits via <see cref="IMatchmakingTelemetry.OnQueueMatchmakingBlocked"/>. Quality math uses
/// <see cref="OrdinalSpreadQuality"/> with normalizer 50 and ratings whose sigma is 0, so
/// Ordinal == mu: e.g. a 2v2 pool {70,0,0,0} best-pairs to means 35 vs 0 → quality 1-35/50 = 0.3.
/// </summary>
public class QueueBlockStatusTests
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
        public RecordingTelemetry Telemetry { get; } = new();
        public Matcher Matcher { get; }

        public Harness(
            int lookAhead = 4,
            double? maxOrdinalSpread = null,
            double? maxMuSpread = null,
            TimeSpan? holdWindow = null,
            double qualityCeiling = 0.99,
            double qStart = 0.4,
            double qFloor = 0.1)
        {
            Matcher = new Matcher(Registry, Index, Balancer, Quality, Clock, () => Telemetry);
            Registry.Register(
                "2v2",
                new MatchShape(2, 2, maxOrdinalSpread: maxOrdinalSpread, maxMuSpread: maxMuSpread),
                new PartitionQualityPolicy(qStart, qFloor, TimeSpan.FromSeconds(60)),
                "gt1",
                lookAheadWindow: lookAhead,
                holdWindow: holdWindow ?? TimeSpan.FromSeconds(10),
                qualityCeiling: qualityCeiling);
        }

        public void Enqueue(string name, double mu) => Matcher.Enqueue(K(name), R(mu), "2v2");
    }

    [Fact]
    public void Imbalanced_full_queue_reports_below_threshold_with_best_quality()
    {
        var h = new Harness();                 // qStart 0.4 → threshold 0.4 at t0
        h.Enqueue("A", 70);
        h.Enqueue("B", 0);
        h.Enqueue("C", 0);
        h.Enqueue("D", 0);

        Assert.Null(h.Matcher.TryProposeMatch());  // best 0.3 < 0.4 → nothing pops

        Assert.True(h.Matcher.TryGetBlockStatus("2v2", out var s));
        Assert.Equal(QueueBlockReason.BelowQualityThreshold, s.Reason);
        Assert.Equal(0.3, s.BestQuality, 6);
        Assert.Equal(0.4, s.Threshold, 6);
        Assert.Null(s.HoldUntil);

        var fired = Assert.Single(h.Telemetry.MatchmakingBlocked);
        Assert.Equal("2v2", fired.Queue);
        Assert.Equal(QueueBlockReason.BelowQualityThreshold, fired.Status.Reason);
    }

    [Fact]
    public void Held_above_threshold_candidate_reports_holding_with_hold_until()
    {
        // {50,0,0,0} → best 0.5 ≥ 0.4 floor but < 0.99 ceiling; pool 4 of 6 lookahead → headroom.
        var h = new Harness(lookAhead: 6, holdWindow: TimeSpan.FromSeconds(10), qualityCeiling: 0.99);
        h.Enqueue("A", 50);
        h.Enqueue("B", 0);
        h.Enqueue("C", 0);
        h.Enqueue("D", 0);

        Assert.Null(h.Matcher.TryProposeMatch());  // held, not popped

        Assert.True(h.Matcher.TryGetBlockStatus("2v2", out var s));
        Assert.Equal(QueueBlockReason.HoldingForArrivals, s.Reason);
        Assert.Equal(0.5, s.BestQuality, 6);
        Assert.Equal(T0.AddSeconds(10), s.HoldUntil!.Value);
    }

    [Fact]
    public void No_partition_within_spread_cap_reports_no_viable_teams()
    {
        // Spread cap 10 < the 50-point gap in the only possible subset → balancer finds nothing.
        var h = new Harness(maxOrdinalSpread: 10);
        h.Enqueue("A", 50);
        h.Enqueue("B", 0);
        h.Enqueue("C", 0);
        h.Enqueue("D", 0);

        Assert.Null(h.Matcher.TryProposeMatch());

        Assert.True(h.Matcher.TryGetBlockStatus("2v2", out var s));
        Assert.Equal(QueueBlockReason.NoViableTeams, s.Reason);
        Assert.Equal(0.0, s.BestQuality, 6);
        Assert.Single(h.Telemetry.MatchmakingBlocked);
    }

    [Fact]
    public void MaxMuSpread_block_reports_skill_spread_too_wide_with_relax_countdown()
    {
        // Only subset is {50,50,50,10}; its 40-point mu spread exceeds the 15 cap, so no roster
        // survives -- but one forms with the cap forgone. That's specifically a MaxMuSpread block,
        // so the reason is SkillSpreadTooWide and HoldUntil carries the relax time (enqueue + 60s).
        var h = new Harness(maxMuSpread: 15);
        h.Enqueue("A", 50);
        h.Enqueue("B", 50);
        h.Enqueue("C", 50);
        h.Enqueue("D", 10);

        Assert.Null(h.Matcher.TryProposeMatch());

        Assert.True(h.Matcher.TryGetBlockStatus("2v2", out var s));
        Assert.Equal(QueueBlockReason.SkillSpreadTooWide, s.Reason);
        Assert.Equal(T0.AddSeconds(60), s.HoldUntil);
        Assert.Single(h.Telemetry.MatchmakingBlocked);
    }

    [Fact]
    public void Skill_spread_block_relaxes_and_forms_a_match_after_relax_time()
    {
        var h = new Harness(maxMuSpread: 15);
        h.Enqueue("A", 50);
        h.Enqueue("B", 50);
        h.Enqueue("C", 50);
        h.Enqueue("D", 10);

        Assert.Null(h.Matcher.TryProposeMatch());                 // blocked: SkillSpreadTooWide

        h.Clock.Advance(TimeSpan.FromSeconds(60));                // reach RelaxTime -> cap forgone
        Assert.NotNull(h.Matcher.TryProposeMatch());              // now forms a match
        Assert.False(h.Matcher.TryGetBlockStatus("2v2", out _));  // cleared on pop
    }

    [Fact]
    public void MaxMuSpread_set_but_below_floor_still_reports_below_threshold()
    {
        // The roster is within the mu cap (spread 20 <= 25), so MaxMuSpread is NOT the blocker;
        // the teams are just imbalanced -> BelowQualityThreshold, not SkillSpreadTooWide.
        var h = new Harness(maxMuSpread: 25, qStart: 0.9);        // threshold 0.9 at t0
        h.Enqueue("A", 20);
        h.Enqueue("B", 0);
        h.Enqueue("C", 0);
        h.Enqueue("D", 0);                                        // best pairs to 10 vs 0 -> q 0.8 < 0.9

        Assert.Null(h.Matcher.TryProposeMatch());
        Assert.True(h.Matcher.TryGetBlockStatus("2v2", out var s));
        Assert.Equal(QueueBlockReason.BelowQualityThreshold, s.Reason);
    }

    [Fact]
    public void Popping_a_held_match_clears_the_block_status()
    {
        var h = new Harness(lookAhead: 6, holdWindow: TimeSpan.FromSeconds(10), qualityCeiling: 0.99);
        h.Enqueue("A", 50);
        h.Enqueue("B", 0);
        h.Enqueue("C", 0);
        h.Enqueue("D", 0);

        Assert.Null(h.Matcher.TryProposeMatch());                 // holding
        Assert.True(h.Matcher.TryGetBlockStatus("2v2", out _));

        h.Clock.Advance(TimeSpan.FromSeconds(11));                // hold window elapses
        Assert.NotNull(h.Matcher.TryProposeMatch());              // pops

        Assert.False(h.Matcher.TryGetBlockStatus("2v2", out _));  // cleared on pop
    }

    [Fact]
    public void Dropping_below_player_count_clears_the_block_status()
    {
        var h = new Harness();
        h.Enqueue("A", 70);
        h.Enqueue("B", 0);
        h.Enqueue("C", 0);
        h.Enqueue("D", 0);

        Assert.Null(h.Matcher.TryProposeMatch());
        Assert.True(h.Matcher.TryGetBlockStatus("2v2", out _));   // blocked while full

        h.Matcher.Dequeue(K("D"), "2v2");                         // now 3 < 4
        Assert.Null(h.Matcher.TryProposeMatch());
        Assert.False(h.Matcher.TryGetBlockStatus("2v2", out _));  // cleared when under-filled
    }

    [Fact]
    public void Same_reason_across_ticks_fires_telemetry_once()
    {
        var h = new Harness();
        h.Enqueue("A", 70);
        h.Enqueue("B", 0);
        h.Enqueue("C", 0);
        h.Enqueue("D", 0);

        h.Matcher.TryProposeMatch();
        h.Matcher.TryProposeMatch();   // same reason, same quality → no re-fire

        Assert.Single(h.Telemetry.MatchmakingBlocked);
    }
}
