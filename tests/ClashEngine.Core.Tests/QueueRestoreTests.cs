using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests;

/// <summary>
/// Post-match restoration of *other* queue memberships: when a match pops, the matcher dequeues
/// the matched players from every queue they were searching, not just the one the match formed
/// from. Finishing the match (any final state) puts them back into those other queues -- the
/// original enqueue was explicit player intent, so unlike <c>?auto</c> (see
/// <see cref="AutoQueueTests"/>) restoration needs no opt-in. Abandoners and players who became
/// ineligible (or whose queue vanished in a hot reload) are not restored.
/// </summary>
public class QueueRestoreTests
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

            // "2v2" pops as soon as four players queue; "4v4" needs eight, so the same four
            // players keep waiting there -- the membership a pop revokes and finalization restores.
            Engine.Queues.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1",
                () => new KillCountEndPolicy(killTarget));
            Engine.Queues.Register(
                "4v4",
                new MatchShape(4, 4),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt2",
                () => new KillCountEndPolicy(killTarget),
                ownerArenaName: "slowarena");
        }

        public void ConnectAll(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
        }

        public void EnqueueAll(string queue, params string[] names)
        {
            foreach (var n in names)
                Assert.Equal(EnqueueResult.Ok, Engine.TryEnqueue(K(n), queue, Clock.UtcNow));
        }

        /// <summary>Pops the 2v2 (the 4v4 stays under-filled), joins everyone, marks Live.</summary>
        public ActiveMatch StartPoppedMatch()
        {
            Engine.Tick(Clock.UtcNow);
            var matchId = Engine.ActiveMatches.Keys.Single();
            var match = Engine.ActiveMatches[matchId];
            foreach (var team in match.Teams)
                foreach (var p in team)
                    Engine.OnPlayerJoinedArena(p, Clock.UtcNow);
            Engine.MarkMatchLive(matchId, Clock.UtcNow);
            return match;
        }

        public IReadOnlyList<PlayerKey> QueueMembers(string queue)
        {
            Engine.Queues.TryGet(queue, out var def);
            return def!.Queue.Snapshot().Select(e => e.Player).ToList();
        }

        public void CompleteByKill(ActiveMatch match)
        {
            Clock.Advance(TimeSpan.FromSeconds(5));
            Engine.OnKill(match.Teams[0][0], match.Teams[1][0], Clock.UtcNow);
        }
    }

    [Fact]
    public void Other_queue_memberships_are_restored_after_a_completed_match()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.EnqueueAll("4v4", "A", "B", "C", "D");
        h.EnqueueAll("2v2", "A", "B", "C", "D");

        var match = h.StartPoppedMatch();
        // The pop dequeued everyone from the 4v4 too (existing behaviour).
        Assert.Empty(h.QueueMembers("4v4"));

        h.CompleteByKill(match);

        // All four are back in the 4v4; nobody re-entered the formation queue (?auto is off).
        Assert.Equal(4, h.QueueMembers("4v4").Count);
        foreach (var n in new[] { "A", "B", "C", "D" })
            Assert.Contains(K(n), h.QueueMembers("4v4"));
        Assert.Empty(h.QueueMembers("2v2"));
    }

    [Fact]
    public void Restore_fires_OnQueueRestored_not_OnQueueAdded_or_OnAutoQueued()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.EnqueueAll("4v4", "A", "B", "C", "D");
        h.EnqueueAll("2v2", "A", "B", "C", "D");

        var match = h.StartPoppedMatch();
        int queueAddsBefore = h.Telemetry.QueueAdds.Count;

        h.CompleteByKill(match);

        Assert.Equal(queueAddsBefore, h.Telemetry.QueueAdds.Count);
        Assert.Empty(h.Telemetry.AutoQueued);
        Assert.Equal(4, h.Telemetry.QueueRestored.Count);
        Assert.All(h.Telemetry.QueueRestored, e => Assert.Equal("4v4", e.Queue));
    }

    [Fact]
    public void Restore_and_auto_queue_compose_independently()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.Engine.AutoQueue.Set(K("A"), true);   // only A opts into ?auto
        h.EnqueueAll("4v4", "A", "B", "C", "D");
        h.EnqueueAll("2v2", "A", "B", "C", "D");

        var match = h.StartPoppedMatch();
        h.CompleteByKill(match);

        // A is in both queues (?auto re-added the formation queue, restore the other); the rest
        // only got their 4v4 spot back.
        Assert.Equal(new[] { K("A") }, h.QueueMembers("2v2"));
        Assert.Equal(4, h.QueueMembers("4v4").Count);
        Assert.Equal(K("A"), Assert.Single(h.Telemetry.AutoQueued).Player);
    }

    [Fact]
    public void Players_queued_only_in_the_formation_queue_get_no_restore()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.EnqueueAll("2v2", "A", "B", "C", "D");

        var match = h.StartPoppedMatch();
        h.CompleteByKill(match);

        Assert.Empty(h.Telemetry.QueueRestored);
        Assert.Empty(h.QueueMembers("4v4"));
    }

    [Fact]
    public void Afk_cancel_restores_readied_players_but_not_the_violator()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.EnqueueAll("4v4", "A", "B", "C", "D");
        h.EnqueueAll("2v2", "A", "B", "C", "D");

        h.Engine.Tick(h.Clock.UtcNow);
        var matchId = h.Engine.ActiveMatches.Keys.Single();
        foreach (var n in new[] { "A", "B", "C", "D" })
            h.Engine.OnPlayerJoinedArena(K(n), h.Clock.UtcNow);

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(h.Engine.CancelMatchAsAfk(matchId, new[] { K("D") }, h.Clock.UtcNow));

        // The match never ran, but the pop still cost everyone their 4v4 spot -- the readied
        // players get it back; the AFK violator (an abandoner, now also in timeout) does not.
        var fourVFour = h.QueueMembers("4v4");
        Assert.Equal(3, fourVFour.Count);
        Assert.DoesNotContain(K("D"), fourVFour);

        // And the existing AFK-cancel behaviour still re-queues the readied at the 2v2's front.
        var twoVTwo = h.QueueMembers("2v2");
        Assert.Equal(3, twoVTwo.Count);
        Assert.DoesNotContain(K("D"), twoVTwo);
    }

    [Fact]
    public void Disconnected_player_is_not_restored()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.EnqueueAll("4v4", "A", "B", "C", "D");
        h.EnqueueAll("2v2", "A", "B", "C", "D");

        var match = h.StartPoppedMatch();

        // A non-scoring participant drops mid-match, then the match completes.
        var leaver = match.Teams[1][1];
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnPlayerDisconnected(leaver, h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Engine.OnKill(match.Teams[0][0], match.Teams[1][0], h.Clock.UtcNow);

        var restored = h.QueueMembers("4v4");
        Assert.Equal(3, restored.Count);
        Assert.DoesNotContain(leaver, restored);
    }

    [Fact]
    public void Queue_removed_by_hot_reload_is_skipped_without_error()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.EnqueueAll("4v4", "A", "B", "C", "D");
        h.EnqueueAll("2v2", "A", "B", "C", "D");

        var match = h.StartPoppedMatch();

        // The 4v4's arena detaches while the match runs (config hot reload).
        h.Engine.Queues.RemoveByOwner("slowarena");

        h.CompleteByKill(match);

        Assert.Empty(h.Telemetry.QueueRestored);
        Assert.Single(h.Telemetry.Ended);   // finalization itself completed normally
    }
}
