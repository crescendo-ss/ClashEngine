using System.Linq;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
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

        // A 1v1 queue (needs 2) so a single waiter just sits there without forming a match, and
        // near-full (>= 4 total) never fires to add noise.
        public Harness(TimeSpan? warn, TimeSpan? cull)
        {
            Engine = new MatchmakingEngine(
                Ratings, Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                telemetry: Telemetry);
            Engine.Queues.Register(
                "1v1",
                new MatchShape(2, 1),
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
}
