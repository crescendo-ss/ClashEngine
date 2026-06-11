using System.Linq;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests.Events;

public class AfkDwellWatchdogTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public InMemoryRatingStore Ratings { get; } = new();
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }

        // Defaults to a 1v1 queue (needs 2) so a single waiter just sits there without forming a
        // match, and near-full (>= 4 total) never fires to add noise. Pass a larger shape to hold
        // several non-matching waiters (e.g. a 4v4 needs 8, so 2-3 sit idle) for isolation tests.
        public Harness(TimeSpan? warn, TimeSpan? cull, MatchShape? shape = null)
        {
            Engine = new MatchmakingEngine(
                Ratings, Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                telemetry: Telemetry);
            Engine.Queues.Register(
                "1v1",
                shape ?? new MatchShape(2, 1),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1",
                () => new KillCountEndPolicy(5),
                afkDwellWarning: warn,
                afkDwellCull: cull);
        }

        public void Connect(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
        }

        public void Enqueue(string n) => Engine.TryEnqueue(K(n), "1v1", Clock.UtcNow);
        public void Advance(TimeSpan d) => Clock.Advance(d);
        public void Tick() => Engine.Tick(Clock.UtcNow);
        public bool StillQueued(string n) =>
            Engine.Queues.TryGet("1v1", out var def) && def.Queue.Contains(K(n));

        // The live queue entry for a still-queued player, so tests can assert their spot
        // (EnqueuedAt / LastSeenAt) survived a neighbor's cull unperturbed.
        public QueueEntry Entry(string n)
        {
            Assert.True(Engine.Queues.TryGet("1v1", out var def));
            return def.Queue.Snapshot().Single(e => e.Player.Equals(K(n)));
        }

        // FIFO order of the still-queued players, to assert a cull doesn't reshuffle survivors.
        public string[] Order()
        {
            Assert.True(Engine.Queues.TryGet("1v1", out var def));
            return def.Queue.Snapshot().Select(e => e.Player.Name).ToArray();
        }
    }

    [Fact]
    public void Dwell_warning_fires_once_at_threshold()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromMinutes(20));
        h.Connect("A");
        h.Enqueue("A");

        h.Advance(TimeSpan.FromMinutes(14));
        h.Tick();
        Assert.Empty(h.Telemetry.DwellWarnings);

        h.Advance(TimeSpan.FromMinutes(1));   // 15m total
        h.Tick();
        var warning = Assert.Single(h.Telemetry.DwellWarnings);
        Assert.Equal(K("A"), warning.Player);
        Assert.Equal("1v1", warning.Queue);
        Assert.Equal(TimeSpan.FromMinutes(15), warning.Dwell);

        h.Advance(TimeSpan.FromMinutes(1));   // 16m, still below cull
        h.Tick();
        Assert.Single(h.Telemetry.DwellWarnings);   // warn-once
    }

    [Fact]
    public void Auto_cull_dequeues_at_cull_threshold_with_afk_cull_reason()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromMinutes(20));
        h.Connect("A");
        h.Enqueue("A");

        h.Advance(TimeSpan.FromMinutes(20));
        h.Tick();

        var removal = Assert.Single(h.Telemetry.QueueRemovals);
        Assert.Equal(K("A"), removal.Player);
        Assert.Equal("1v1", removal.Queue);
        Assert.Equal(QueueRemovalReason.AfkCull, removal.Reason);
        Assert.False(h.StillQueued("A"));
    }

    [Fact]
    public void Requeue_resets_dwell_timer()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromHours(1));
        h.Connect("A");
        h.Enqueue("A");

        h.Advance(TimeSpan.FromMinutes(15));
        h.Tick();
        Assert.Single(h.Telemetry.DwellWarnings);

        // Player re-queues (a fresh EnqueuedAt), which should re-arm the warning.
        h.Engine.Dequeue(K("A"), "1v1", h.Clock.UtcNow);
        h.Enqueue("A");

        h.Advance(TimeSpan.FromMinutes(14));   // only 14m since the re-queue
        h.Tick();
        Assert.Single(h.Telemetry.DwellWarnings);   // not yet

        h.Advance(TimeSpan.FromMinutes(1));    // 15m since the re-queue
        h.Tick();
        Assert.Equal(2, h.Telemetry.DwellWarnings.Count);
    }

    [Fact]
    public void Replaying_play_refreshes_LastSeenAt_but_preserves_EnqueuedAt()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromHours(1));
        h.Connect("A");
        h.Enqueue("A");

        h.Advance(TimeSpan.FromMinutes(5));
        h.Enqueue("A");   // repeat ?play while queued -> liveness ping at T0+5m

        Assert.True(h.Engine.Queues.TryGet("1v1", out var def));
        var entry = def.Queue.Snapshot().Single();
        Assert.Equal(T0, entry.EnqueuedAt);                            // true time-in-queue preserved
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), entry.LastSeenAt);  // AFK dwell clock advanced
    }

    [Fact]
    public void Replaying_play_defers_the_dwell_warning()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromHours(1));
        h.Connect("A");
        h.Enqueue("A");

        h.Advance(TimeSpan.FromMinutes(14));
        h.Tick();
        Assert.Empty(h.Telemetry.DwellWarnings);   // not warned yet

        h.Enqueue("A");   // liveness ping at T0+14m resets the dwell clock

        h.Advance(TimeSpan.FromMinutes(14));   // 28m in queue, but only 14m since the refresh
        h.Tick();
        Assert.Empty(h.Telemetry.DwellWarnings);   // refresh deferred the warning

        h.Advance(TimeSpan.FromMinutes(1));    // 15m since the refresh
        h.Tick();
        Assert.Single(h.Telemetry.DwellWarnings);
    }

    [Fact]
    public void Replaying_play_defers_the_cull()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromMinutes(20));
        h.Connect("A");
        h.Enqueue("A");

        h.Advance(TimeSpan.FromMinutes(19));
        h.Tick();
        Assert.True(h.StillQueued("A"));

        h.Enqueue("A");   // liveness ping just shy of the cull threshold

        h.Advance(TimeSpan.FromMinutes(19));   // 38m in queue, but 19m since refresh < 20m cull
        h.Tick();
        Assert.True(h.StillQueued("A"));
        Assert.DoesNotContain(h.Telemetry.QueueRemovals, r => r.Reason == QueueRemovalReason.AfkCull);
    }

    [Fact]
    public void Zero_warn_disables_the_watchdog()
    {
        var h = new Harness(warn: TimeSpan.Zero, cull: TimeSpan.Zero);
        h.Connect("A");
        h.Enqueue("A");

        h.Advance(TimeSpan.FromHours(1));
        h.Tick();

        Assert.Empty(h.Telemetry.DwellWarnings);
        Assert.Empty(h.Telemetry.QueueRemovals);
        Assert.True(h.StillQueued("A"));
    }

    [Fact]
    public void Zero_cull_keeps_warning_but_never_dequeues()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.Zero);
        h.Connect("A");
        h.Enqueue("A");

        h.Advance(TimeSpan.FromMinutes(15));
        h.Tick();
        Assert.Single(h.Telemetry.DwellWarnings);

        h.Advance(TimeSpan.FromHours(1));
        h.Tick();
        Assert.Empty(h.Telemetry.QueueRemovals);
        Assert.Single(h.Telemetry.DwellWarnings);
        Assert.True(h.StillQueued("A"));
    }

    [Fact]
    public void Player_matched_in_same_tick_is_not_culled()
    {
        // cull threshold already crossed, but the pair forms a match this tick (proposal popping
        // runs before the AFK sweep), so they leave as Matched, never AfkCull.
        var h = new Harness(warn: TimeSpan.FromMinutes(5), cull: TimeSpan.FromMinutes(10));
        h.Connect("A", "B");
        h.Enqueue("A");
        h.Enqueue("B");

        h.Advance(TimeSpan.FromMinutes(11));
        h.Tick();

        Assert.Single(h.Telemetry.Proposed);
        Assert.DoesNotContain(h.Telemetry.QueueRemovals, r => r.Reason == QueueRemovalReason.AfkCull);
        Assert.All(h.Telemetry.QueueRemovals, r => Assert.Equal(QueueRemovalReason.Matched, r.Reason));
    }

    // A 4v4 shape (needs 8) holds several waiters without ever forming a match, so we can watch a
    // cull land on one of them in isolation.
    private static readonly MatchShape FourV4 = new(2, 4);

    [Fact]
    public void Culling_an_idle_player_leaves_a_non_idle_neighbor_in_their_spot()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromMinutes(20), shape: FourV4);
        h.Connect("A", "B");
        h.Enqueue("A");                       // A enqueues at T0

        h.Advance(TimeSpan.FromMinutes(10));
        h.Enqueue("B");                       // B enqueues 10m later, at T0+10m

        h.Advance(TimeSpan.FromMinutes(10));  // now T0+20m: A has idled 20m (cull), B only 10m
        h.Tick();

        // Only the idle player A is culled; B is untouched.
        var removal = Assert.Single(h.Telemetry.QueueRemovals);
        Assert.Equal(K("A"), removal.Player);
        Assert.Equal(QueueRemovalReason.AfkCull, removal.Reason);
        Assert.False(h.StillQueued("A"));

        // B keeps their spot: still queued, original EnqueuedAt/LastSeenAt intact, now at the head.
        Assert.True(h.StillQueued("B"));
        Assert.Equal(new[] { "B" }, h.Order());
        Assert.Equal(T0 + TimeSpan.FromMinutes(10), h.Entry("B").EnqueuedAt);
        Assert.Equal(T0 + TimeSpan.FromMinutes(10), h.Entry("B").LastSeenAt);
    }

    [Fact]
    public void A_refreshed_player_keeps_their_spot_while_an_idle_neighbor_is_culled()
    {
        // The reported scenario: two players queue together; one re-affirms with ?play near the
        // cull horizon and must survive while the genuinely-idle one is removed -- with their
        // original time-in-queue (EnqueuedAt) and FIFO position preserved.
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromMinutes(20), shape: FourV4);
        h.Connect("A", "B", "C");
        h.Enqueue("A");
        h.Enqueue("B");
        h.Enqueue("C");                       // all three enqueue at T0, in A,B,C order

        h.Advance(TimeSpan.FromMinutes(12));
        h.Enqueue("B");                       // B re-issues ?play at T0+12m -> liveness ping (Touch)

        h.Advance(TimeSpan.FromMinutes(8));   // now T0+20m: A and C idled 20m (cull), B only 8m
        h.Tick();

        // A and C are culled for inactivity; the refreshed B is spared.
        Assert.Equal(2, h.Telemetry.QueueRemovals.Count);
        Assert.All(h.Telemetry.QueueRemovals, r => Assert.Equal(QueueRemovalReason.AfkCull, r.Reason));
        Assert.Contains(h.Telemetry.QueueRemovals, r => r.Player.Equals(K("A")));
        Assert.Contains(h.Telemetry.QueueRemovals, r => r.Player.Equals(K("C")));
        Assert.False(h.StillQueued("A"));
        Assert.False(h.StillQueued("C"));

        // B's spot is intact: refresh moved only the AFK clock, not the original join time/position.
        Assert.True(h.StillQueued("B"));
        Assert.Equal(new[] { "B" }, h.Order());
        Assert.Equal(T0, h.Entry("B").EnqueuedAt);                              // true time-in-queue preserved
        Assert.Equal(T0 + TimeSpan.FromMinutes(12), h.Entry("B").LastSeenAt);   // only the dwell clock advanced
    }

    // ---- OnPlayerActivity: in-game activity (rotation/fire/chat, forwarded by the host
    // adapter) is the same liveness ping as a repeat ?play, across every queue at once.

    [Fact]
    public void Activity_refreshes_LastSeenAt_in_every_queue_preserving_EnqueuedAt()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromHours(1));
        h.Engine.Queues.Register(
            "duo",
            new MatchShape(2, 1),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt2",
            () => new KillCountEndPolicy(5),
            afkDwellWarning: TimeSpan.FromMinutes(15),
            afkDwellCull: TimeSpan.FromHours(1));
        h.Connect("A");
        h.Enqueue("A");
        h.Engine.TryEnqueue(K("A"), "duo", h.Clock.UtcNow);

        h.Advance(TimeSpan.FromMinutes(5));
        Assert.True(h.Engine.OnPlayerActivity(K("A"), h.Clock.UtcNow));

        Assert.Equal(T0, h.Entry("A").EnqueuedAt);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), h.Entry("A").LastSeenAt);

        Assert.True(h.Engine.Queues.TryGet("duo", out var duo));
        var duoEntry = duo.Queue.Snapshot().Single(e => e.Player.Equals(K("A")));
        Assert.Equal(T0, duoEntry.EnqueuedAt);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), duoEntry.LastSeenAt);
    }

    [Fact]
    public void Activity_defers_the_dwell_warning()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromHours(1));
        h.Connect("A");
        h.Enqueue("A");

        h.Advance(TimeSpan.FromMinutes(14));
        h.Tick();
        Assert.Empty(h.Telemetry.DwellWarnings);

        Assert.True(h.Engine.OnPlayerActivity(K("A"), h.Clock.UtcNow));   // turned/fired/chatted

        h.Advance(TimeSpan.FromMinutes(14));   // 28m in queue, 14m since the activity
        h.Tick();
        Assert.Empty(h.Telemetry.DwellWarnings);

        h.Advance(TimeSpan.FromMinutes(1));    // 15m since the activity
        h.Tick();
        Assert.Single(h.Telemetry.DwellWarnings);
    }

    [Fact]
    public void Activity_defers_the_cull()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromMinutes(20));
        h.Connect("A");
        h.Enqueue("A");

        h.Advance(TimeSpan.FromMinutes(19));
        h.Tick();
        Assert.True(h.StillQueued("A"));

        Assert.True(h.Engine.OnPlayerActivity(K("A"), h.Clock.UtcNow));

        h.Advance(TimeSpan.FromMinutes(19));   // 38m in queue, 19m since activity < 20m cull
        h.Tick();
        Assert.True(h.StillQueued("A"));
        Assert.DoesNotContain(h.Telemetry.QueueRemovals, r => r.Reason == QueueRemovalReason.AfkCull);
    }

    [Fact]
    public void Activity_after_a_warning_rearms_it()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromHours(1));
        h.Connect("A");
        h.Enqueue("A");

        h.Advance(TimeSpan.FromMinutes(15));
        h.Tick();
        Assert.Single(h.Telemetry.DwellWarnings);

        Assert.True(h.Engine.OnPlayerActivity(K("A"), h.Clock.UtcNow));   // fresh liveness epoch

        h.Advance(TimeSpan.FromMinutes(14));
        h.Tick();
        Assert.Single(h.Telemetry.DwellWarnings);   // not yet: 14m since the activity

        h.Advance(TimeSpan.FromMinutes(1));
        h.Tick();
        Assert.Equal(2, h.Telemetry.DwellWarnings.Count);   // second warning, full period later
    }

    [Fact]
    public void Activity_for_an_unqueued_player_is_a_noop_returning_false()
    {
        var h = new Harness(warn: TimeSpan.FromMinutes(15), cull: TimeSpan.FromMinutes(20));
        h.Connect("A");

        Assert.False(h.Engine.OnPlayerActivity(K("A"), h.Clock.UtcNow));
        Assert.False(h.Engine.OnPlayerActivity(K("Nobody"), h.Clock.UtcNow));
    }
}
