using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests;

/// <summary>
/// Per-player <c>?autoqueue</c> behaviour: opted-in players are re-enqueued into the queue their
/// finished match formed from, opted-out players are not, and a staging-AFK violation force-disables
/// the preference. Independent of KOTH winner promotion (see <see cref="KothModeTests"/>).
/// </summary>
public class AutoQueueTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public InMemoryRatingStore Ratings { get; } = new();
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }

        public Harness(int killTarget = 1)
        {
            Engine = new MatchmakingEngine(
                Ratings, Clock,
                // StagingAfk registered so an AFK cancel assesses the AFK ladder (and trips the
                // auto-queue-off hook) rather than falling back to the Abandonment kind.
                new[]
                {
                    PenaltyPolicy.DefaultAbandonment,
                    PenaltyPolicy.DefaultGriefing,
                    PenaltyPolicy.DefaultStagingAfk,
                },
                quality: new OrdinalSpreadQuality(),
                telemetry: Telemetry,
                joinTimeout: TimeSpan.FromMinutes(1),
                graceWindow: TimeSpan.FromSeconds(30));

            Engine.Queues.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1",
                () => new KillCountEndPolicy(killTarget));
        }

        public void ConnectAll(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
        }

        public void EnqueueAll(params string[] names)
        {
            foreach (var n in names) Engine.TryEnqueue(K(n), "2v2", Clock.UtcNow);
        }

        /// <summary>Enqueues, pops, joins, and marks the match Live. Returns the live match.</summary>
        public ActiveMatch FormAndStartMatch(params string[] names)
        {
            EnqueueAll(names);
            Engine.Tick(Clock.UtcNow);
            var matchId = Engine.ActiveMatches.Keys.Single();
            var match = Engine.ActiveMatches[matchId];
            foreach (var team in match.Teams)
                foreach (var p in team)
                    Engine.OnPlayerJoinedArena(p, Clock.UtcNow);
            Engine.MarkMatchLive(matchId, Clock.UtcNow);
            return match;
        }
    }

    [Fact]
    public void AutoQueue_is_off_by_default_and_toggles()
    {
        var h = new Harness();
        Assert.False(h.Engine.AutoQueue.IsEnabled(K("A")));

        h.Engine.AutoQueue.Set(K("A"), true);
        Assert.True(h.Engine.AutoQueue.IsEnabled(K("A")));

        h.Engine.AutoQueue.Set(K("A"), false);
        Assert.False(h.Engine.AutoQueue.IsEnabled(K("A")));
    }

    [Fact]
    public void Opted_in_player_is_re_enqueued_after_a_completed_match()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.Engine.AutoQueue.Set(K("A"), true);   // only A opts in

        var match = h.FormAndStartMatch("A", "B", "C", "D");
        var killer = match.Teams[0][0];
        var victim = match.Teams[1][0];

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(killer, victim, h.Clock.UtcNow);

        // Match completed; only A (the opt-in) is back in the queue, regardless of win/loss.
        h.Engine.Queues.TryGet("2v2", out var def);
        var snap = def!.Queue.Snapshot();
        Assert.Single(snap);
        Assert.Contains(snap, e => e.Player == K("A"));
    }

    [Fact]
    public void Players_without_the_preference_are_not_re_enqueued()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");   // nobody opts in

        var match = h.FormAndStartMatch("A", "B", "C", "D");
        var killer = match.Teams[0][0];
        var victim = match.Teams[1][0];

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(killer, victim, h.Clock.UtcNow);

        h.Engine.Queues.TryGet("2v2", out var def);
        Assert.Empty(def!.Queue.Snapshot());
        Assert.Empty(h.Telemetry.AutoQueued);
    }

    [Fact]
    public void Auto_re_enqueue_fires_OnAutoQueued_not_OnQueueAdded()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.Engine.AutoQueue.Set(K("A"), true);

        var match = h.FormAndStartMatch("A", "B", "C", "D");
        int queueAddsBefore = h.Telemetry.QueueAdds.Count;
        var killer = match.Teams[0][0];
        var victim = match.Teams[1][0];

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(killer, victim, h.Clock.UtcNow);

        // The re-add surfaces as OnAutoQueued, not the generic OnQueueAdded, so the adapter can
        // send an auto-queue-specific notice.
        Assert.Equal(queueAddsBefore, h.Telemetry.QueueAdds.Count);
        var queued = Assert.Single(h.Telemetry.AutoQueued);
        Assert.Equal(K("A"), queued.Player);
        Assert.Equal("2v2", queued.Queue);
    }

    [Fact]
    public void Staging_afk_violation_disables_autoqueue_and_notifies()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.Engine.AutoQueue.Set(K("D"), true);

        // Pop and join a match but leave it Forming (no MarkMatchLive) so the staging AFK cancel
        // path applies.
        h.EnqueueAll("A", "B", "C", "D");
        h.Engine.Tick(h.Clock.UtcNow);
        var matchId = h.Engine.ActiveMatches.Keys.Single();
        foreach (var n in new[] { "A", "B", "C", "D" })
            h.Engine.OnPlayerJoinedArena(K(n), h.Clock.UtcNow);

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(h.Engine.CancelMatchAsAfk(matchId, new[] { K("D") }, h.Clock.UtcNow));

        // D was flagged AFK -> preference forced off + the player is notified to re-enable it.
        Assert.False(h.Engine.AutoQueue.IsEnabled(K("D")));
        Assert.Contains(K("D"), h.Telemetry.AutoQueueDisabledByAfk);

        // And the AFK violator is not auto-re-enqueued (Cancelled matches don't auto-queue; only
        // the readied A/B/C are put back at the front by the existing AFK-cancel path).
        h.Engine.Queues.TryGet("2v2", out var def);
        Assert.DoesNotContain(def!.Queue.Snapshot(), e => e.Player == K("D"));
        Assert.Empty(h.Telemetry.AutoQueued);
    }

    [Fact]
    public void Staging_afk_does_not_notify_a_player_who_had_autoqueue_off()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");   // D never opted in

        h.EnqueueAll("A", "B", "C", "D");
        h.Engine.Tick(h.Clock.UtcNow);
        var matchId = h.Engine.ActiveMatches.Keys.Single();
        foreach (var n in new[] { "A", "B", "C", "D" })
            h.Engine.OnPlayerJoinedArena(K(n), h.Clock.UtcNow);

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(h.Engine.CancelMatchAsAfk(matchId, new[] { K("D") }, h.Clock.UtcNow));

        // Nothing to disable -> no notification.
        Assert.Empty(h.Telemetry.AutoQueueDisabledByAfk);
        Assert.False(h.Engine.AutoQueue.IsEnabled(K("D")));
    }

    [Fact]
    public void Voluntary_spec_during_staging_keeps_autoqueue_while_in_ship_afk_loses_it()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.Engine.AutoQueue.Set(K("C"), true);   // C: sat in-ship, never moved -> genuine AFK
        h.Engine.AutoQueue.Set(K("D"), true);   // D: specced out -> a deliberate departure

        h.EnqueueAll("A", "B", "C", "D");
        h.Engine.Tick(h.Clock.UtcNow);
        var matchId = h.Engine.ActiveMatches.Keys.Single();
        foreach (var n in new[] { "A", "B", "C", "D" })
            h.Engine.OnPlayerJoinedArena(K(n), h.Clock.UtcNow);

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        // Both C and D failed staging and are penalised, but only C is named in the confirmed-AFK
        // subset; D specced out (something an idle player can't do) and so is a voluntary leaver.
        Assert.True(h.Engine.CancelMatchAsAfk(
            matchId, new[] { K("C"), K("D") }, h.Clock.UtcNow, idleAfkPlayers: new[] { K("C") }));

        // Genuine AFK: ?auto forced off + the player is notified to re-enable it.
        Assert.False(h.Engine.AutoQueue.IsEnabled(K("C")));
        Assert.Contains(K("C"), h.Telemetry.AutoQueueDisabledByAfk);

        // Voluntary leaver: ?auto survives and no disable notice is sent -- they chose to leave,
        // they aren't away from the keyboard.
        Assert.True(h.Engine.AutoQueue.IsEnabled(K("D")));
        Assert.DoesNotContain(K("D"), h.Telemetry.AutoQueueDisabledByAfk);

        // Both still took the StagingAfk timeout -- the distinction is ?auto-only, not penalty.
        Assert.True(h.Engine.Penalties.IsInTimeout(K("C"), h.Clock.UtcNow));
        Assert.True(h.Engine.Penalties.IsInTimeout(K("D"), h.Clock.UtcNow));
    }
}
