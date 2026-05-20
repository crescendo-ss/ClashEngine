using ClashEngine.Core;
using ClashEngine.Core.Eligibility;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests;

public class MatchmakingEngineTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public InMemoryRatingStore Ratings { get; } = new();
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }

        public Harness(
            TimeSpan? joinTimeout = null,
            TimeSpan? graceWindow = null,
            int killTarget = 5)
        {
            Engine = new MatchmakingEngine(
                Ratings, Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                quality: new OrdinalSpreadQuality(),
                telemetry: Telemetry,
                joinTimeout: joinTimeout ?? TimeSpan.FromMinutes(1),
                graceWindow: graceWindow ?? TimeSpan.FromSeconds(30));

            Engine.Queues.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1",
                () => new KillCountEndPolicy(killTarget));
        }

        public void Connect(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
        }

        public void Enqueue(string name, string queue = "2v2") =>
            Engine.TryEnqueue(K(name), queue, Clock.UtcNow);

        /// <summary>
        /// Drives the engine through the same sequence the orchestrator does at end-of-Countdown:
        /// each player gets joined-arena (Pending -> Active), then the match is marked Live. Use
        /// when a test just needs an in-progress match without exercising the join/timeout paths.
        /// </summary>
        public ActiveMatch StartProposedMatch()
        {
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
    public void Full_pool_proposes_a_match_and_marks_players_InMatch()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        foreach (var n in new[] { "A", "B", "C", "D" })
            Assert.Equal(EnqueueResult.Ok, h.Engine.TryEnqueue(K(n), "2v2", T0));

        h.Engine.Tick(T0);

        Assert.Single(h.Telemetry.Proposed);
        foreach (var n in new[] { "A", "B", "C", "D" })
            Assert.Equal(EligibilityStatus.InMatch, h.Engine.CheckEligibility(K(n)).Status);
    }

    [Fact]
    public void OnPlayerJoinedArena_does_not_flip_to_Live_without_MarkMatchLive()
    {
        // Engine state is gameplay-live only after the orchestrator's GO! call. Up to then we
        // stay Forming so Live-only paths (kill processing, team-collapse, distance sampling)
        // can't fire pre-GO even if a ship-set callback already fired for every player.
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        foreach (var n in new[] { "A", "B", "C", "D" }) h.Enqueue(n);
        h.Engine.Tick(T0);

        var matchId = h.Engine.ActiveMatches.Keys.Single();

        h.Clock.Advance(TimeSpan.FromSeconds(2));
        foreach (var n in new[] { "A", "B", "C", "D" })
            h.Engine.OnPlayerJoinedArena(K(n), h.Clock.UtcNow);

        Assert.Empty(h.Telemetry.Started);
        Assert.Equal(MatchState.Forming, h.Engine.ActiveMatches[matchId].State);

        // Orchestrator's GO! tick.
        h.Clock.Advance(TimeSpan.FromSeconds(13));
        Assert.True(h.Engine.MarkMatchLive(matchId, h.Clock.UtcNow));

        Assert.Single(h.Telemetry.Started);
        Assert.Equal(MatchState.Live, h.Telemetry.Started[0].State);
        Assert.Equal(h.Clock.UtcNow, h.Telemetry.Started[0].StartedAt);
    }

    [Fact]
    public void MarkMatchLive_returns_false_when_a_player_never_joined()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        foreach (var n in new[] { "A", "B", "C", "D" }) h.Enqueue(n);
        h.Engine.Tick(T0);
        var matchId = h.Engine.ActiveMatches.Keys.Single();

        h.Clock.Advance(TimeSpan.FromSeconds(2));
        // Only 3 of 4 players reached Active.
        h.Engine.OnPlayerJoinedArena(K("A"), h.Clock.UtcNow);
        h.Engine.OnPlayerJoinedArena(K("B"), h.Clock.UtcNow);
        h.Engine.OnPlayerJoinedArena(K("C"), h.Clock.UtcNow);

        Assert.False(h.Engine.MarkMatchLive(matchId, h.Clock.UtcNow));
        Assert.Empty(h.Telemetry.Started);
        Assert.Equal(MatchState.Forming, h.Engine.ActiveMatches[matchId].State);
    }

    [Fact]
    public void End_to_end_completes_match_updates_ratings_and_releases_players()
    {
        var h = new Harness(killTarget: 3);
        h.Connect("A", "B", "C", "D");
        foreach (var n in new[] { "A", "B", "C", "D" }) h.Enqueue(n);
        h.Engine.Tick(T0);

        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.StartProposedMatch();

        var teams = h.Telemetry.Started[0].Teams;
        var winners = teams[0];
        var losers = teams[1];

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        h.Engine.OnKill(winners[0], losers[0], h.Clock.UtcNow);
        h.Engine.OnKill(winners[0], losers[1], h.Clock.UtcNow);
        h.Engine.OnKill(winners[1], losers[0], h.Clock.UtcNow);

        Assert.Single(h.Telemetry.Ended);
        Assert.Equal(MatchState.Completed, h.Telemetry.Ended[0].FinalState);

        // Ratings updated: winner mu rises, loser mu falls.
        Assert.True(h.Ratings.Get(winners[0], "gt1").Mu > Rating.Default.Mu);
        Assert.True(h.Ratings.Get(losers[0], "gt1").Mu < Rating.Default.Mu);

        // Players are no longer in match.
        foreach (var p in winners.Concat(losers))
            Assert.Equal(EligibilityStatus.Available, h.Engine.CheckEligibility(p).Status);
    }

    [Fact]
    public void Player_in_match_cannot_queue_again()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        foreach (var n in new[] { "A", "B", "C", "D" }) h.Enqueue(n);
        h.Engine.Tick(T0);

        Assert.Equal(EnqueueResult.InMatch, h.Engine.TryEnqueue(K("A"), "2v2", T0));
    }

    [Fact]
    public void Disconnected_player_cannot_queue()
    {
        var h = new Harness();
        Assert.Equal(EnqueueResult.NotConnected, h.Engine.TryEnqueue(K("Ghost"), "2v2", T0));
    }

    [Fact]
    public void Player_in_timeout_cannot_queue()
    {
        var h = new Harness();
        h.Connect("A");
        h.Engine.Penalties.RecordPenalty(K("A"), PenaltyKind.Abandonment, T0);

        Assert.Equal(EnqueueResult.InTimeout, h.Engine.TryEnqueue(K("A"), "2v2", T0));
    }

    [Fact]
    public void Unknown_queue_returns_UnknownQueue()
    {
        var h = new Harness();
        h.Connect("A");
        Assert.Equal(EnqueueResult.UnknownQueue, h.Engine.TryEnqueue(K("A"), "ghost", T0));
    }

    [Fact]
    public void Already_queued_returns_AlreadyQueued()
    {
        var h = new Harness();
        h.Connect("A");
        Assert.Equal(EnqueueResult.Ok, h.Engine.TryEnqueue(K("A"), "2v2", T0));
        Assert.Equal(EnqueueResult.AlreadyQueued, h.Engine.TryEnqueue(K("A"), "2v2", T0));
    }

    [Fact]
    public void Queue_membership_persists_across_OnPlayerLeftArena()
    {
        // Queue membership only drops on ?cancel, disconnect, match formation, or eligibility
        // changes -- not on arena hops. Verifies a queued player can ?go elsewhere and back
        // without losing their spot.
        var h = new Harness();
        h.Connect("A");
        Assert.Equal(EnqueueResult.Ok, h.Engine.TryEnqueue(K("A"), "2v2", T0));

        h.Engine.OnPlayerLeftArena(K("A"), T0);

        h.Engine.Queues.TryGet("2v2", out var def);
        Assert.Single(def!.Queue.Snapshot());
        Assert.True(def.Queue.Contains(K("A")));
    }

    [Fact]
    public void Player_failing_to_join_in_time_cancels_match_and_penalizes_them()
    {
        var h = new Harness(joinTimeout: TimeSpan.FromMinutes(1));
        h.Connect("A", "B", "C", "D");
        foreach (var n in new[] { "A", "B", "C", "D" }) h.Enqueue(n);
        h.Engine.Tick(T0);

        // 3 of 4 join.
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnPlayerJoinedArena(K("A"), h.Clock.UtcNow);
        h.Engine.OnPlayerJoinedArena(K("B"), h.Clock.UtcNow);
        h.Engine.OnPlayerJoinedArena(K("C"), h.Clock.UtcNow);

        // Time passes; D never joins.
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Engine.Tick(h.Clock.UtcNow);

        Assert.Single(h.Telemetry.Ended);
        Assert.Equal(MatchState.Cancelled, h.Telemetry.Ended[0].FinalState);

        Assert.Single(h.Telemetry.Abandonments);
        Assert.Equal(K("D"), h.Telemetry.Abandonments[0].Player);

        // D is now in timeout; the others are Available.
        Assert.Equal(EligibilityStatus.InTimeout, h.Engine.CheckEligibility(K("D")).Status);
        foreach (var n in new[] { "A", "B", "C" })
            Assert.Equal(EligibilityStatus.Available, h.Engine.CheckEligibility(K(n)).Status);
    }

    [Fact]
    public void Disconnect_during_live_match_starts_grace_then_abandons_on_tick()
    {
        var h = new Harness(graceWindow: TimeSpan.FromSeconds(30), killTarget: 100);
        h.Connect("A", "B", "C", "D");
        foreach (var n in new[] { "A", "B", "C", "D" }) h.Enqueue(n);
        h.Engine.Tick(T0);

        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.StartProposedMatch();

        var teams = h.Telemetry.Started[0].Teams;
        var leaver = teams[0][0];

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnPlayerDisconnected(leaver, h.Clock.UtcNow);

        // Within grace: still Live (eligibility says InMatch since they're still mapped).
        h.Engine.Tick(h.Clock.UtcNow);
        Assert.Empty(h.Telemetry.Ended);

        // After grace expires AND the rest of the team also leaves → other team wins by forfeit.
        h.Engine.OnPlayerDisconnected(teams[0][1], h.Clock.UtcNow);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Engine.Tick(h.Clock.UtcNow);

        Assert.Single(h.Telemetry.Ended);
        Assert.Equal(MatchState.Completed, h.Telemetry.Ended[0].FinalState);
        // SubspaceServer can't tell quit from network drop, so a player who leaves and never
        // returns within the grace window is recorded as an abandoner regardless of cause.
        Assert.Equal(2, h.Telemetry.Abandonments.Count);
    }

    [Fact]
    public void Multi_queue_search_pop_removes_from_other_queues()
    {
        var h = new Harness();
        h.Engine.Queues.Register(
            "duel",
            new MatchShape(2, 1),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt2",
            () => new KillCountEndPolicy(3));

        h.Connect("A", "B", "C", "D");
        h.Engine.TryEnqueue(K("A"), "2v2", T0);
        h.Engine.TryEnqueue(K("A"), "duel", T0);
        h.Engine.TryEnqueue(K("B"), "2v2", T0);
        h.Engine.TryEnqueue(K("C"), "2v2", T0);
        h.Engine.TryEnqueue(K("D"), "2v2", T0);

        h.Engine.Tick(T0);

        // 2v2 pops and A is in match.
        Assert.Single(h.Telemetry.Proposed);
        Assert.Equal(EligibilityStatus.InMatch, h.Engine.CheckEligibility(K("A")).Status);

        // A should also have been removed from duel queue.
        h.Engine.Queues.TryGet("duel", out var duel);
        Assert.False(duel!.Queue.Contains(K("A")));
    }

    [Fact]
    public void Tick_with_no_full_queues_is_a_noop()
    {
        var h = new Harness();
        h.Connect("A");
        h.Enqueue("A");
        h.Engine.Tick(T0);

        Assert.Empty(h.Telemetry.Proposed);
        Assert.Empty(h.Telemetry.Started);
        Assert.Empty(h.Telemetry.Ended);
    }

    [Fact]
    public void Disconnect_while_queued_silently_dequeues()
    {
        var h = new Harness();
        h.Connect("A");
        h.Enqueue("A");
        Assert.Single(h.Telemetry.QueueAdds);

        h.Engine.OnPlayerDisconnected(K("A"), T0.AddSeconds(5));

        h.Engine.Queues.TryGet("2v2", out var def);
        Assert.False(def!.Queue.Contains(K("A")));
    }

    [Fact]
    public void CancelMatchAsAfk_succeeds_during_staging_when_all_players_have_joined()
    {
        // Pre-architectural-fix this scenario was broken: the engine flipped to Live as soon as
        // every player was placed onto a ship, so the orchestrator's staging-end AFK call hit the
        // "State != Forming" guard and silently no-oped. With the engine now staying Forming
        // until MarkMatchLive is invoked at GO!, CancelMatchAsAfk runs cleanly.
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        foreach (var n in new[] { "A", "B", "C", "D" }) h.Enqueue(n);
        h.Engine.Tick(T0);
        var matchId = h.Engine.ActiveMatches.Keys.Single();

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        foreach (var n in new[] { "A", "B", "C", "D" })
            h.Engine.OnPlayerJoinedArena(K(n), h.Clock.UtcNow);
        Assert.Equal(MatchState.Forming, h.Engine.ActiveMatches[matchId].State);

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(h.Engine.CancelMatchAsAfk(matchId, new[] { K("D") }, h.Clock.UtcNow));

        Assert.Single(h.Telemetry.Ended);
        Assert.Equal(MatchState.Cancelled, h.Telemetry.Ended[0].FinalState);
        Assert.Equal(K("D"), Assert.Single(h.Telemetry.Ended[0].AbandonedBy));

        foreach (var n in new[] { "A", "B", "C" })
            Assert.Equal(EligibilityStatus.Available, h.Engine.CheckEligibility(K(n)).Status);
        Assert.Equal(EligibilityStatus.InTimeout, h.Engine.CheckEligibility(K("D")).Status);

        // Readied players auto-re-enqueue at the front; AFK player is NOT re-added.
        h.Engine.Queues.TryGet("2v2", out var def);
        var snapshot = def!.Queue.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Contains(K("A"), new[] { snapshot[0].Player, snapshot[1].Player, snapshot[2].Player });
        Assert.Contains(K("B"), new[] { snapshot[0].Player, snapshot[1].Player, snapshot[2].Player });
        Assert.Contains(K("C"), new[] { snapshot[0].Player, snapshot[1].Player, snapshot[2].Player });
        Assert.False(def.Queue.Contains(K("D")));

        // Each readied player gets an OnQueueAdded telemetry on top of the original three.
        Assert.Equal(7, h.Telemetry.QueueAdds.Count);
        Assert.All(h.Telemetry.QueueAdds.TakeLast(3), e => Assert.Equal("2v2", e.Queue));
    }

    [Fact]
    public void CancelMatchAsAfk_re_enqueues_readied_party_at_front_with_group_preserved()
    {
        // A registered party (A, B) + solo C and D form a 2v2. D goes AFK; the cancel should
        // put A, B, C back in the queue with A and B still sharing the original GroupId so
        // the matcher keeps trying to seat them on the same team. (Mirrors KOTH winner re-
        // enqueue: GroupId is read from GroupRegistry.GroupOf, which is populated by the
        // Invite/Accept flow that real `?party` -> `?play` uses.)
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        Assert.Equal(InviteResult.Sent, h.Engine.InviteToGroup(K("A"), K("B"), T0));
        Assert.Equal(AcceptResult.Joined, h.Engine.AcceptInvite(K("B"), K("A"), T0, out var partyId));
        Assert.Equal(EnqueueResult.Ok,
            h.Engine.TryEnqueueGroup(new[] { K("A"), K("B") }, "2v2", T0, out _, existingGroup: partyId));
        h.Enqueue("C");
        h.Enqueue("D");
        h.Engine.Tick(T0);

        var matchId = h.Engine.ActiveMatches.Keys.Single();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        foreach (var n in new[] { "A", "B", "C", "D" })
            h.Engine.OnPlayerJoinedArena(K(n), h.Clock.UtcNow);

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(h.Engine.CancelMatchAsAfk(matchId, new[] { K("D") }, h.Clock.UtcNow));

        h.Engine.Queues.TryGet("2v2", out var def);
        var snapshot = def!.Queue.Snapshot();
        Assert.Equal(3, snapshot.Count);
        var aEntry = snapshot.Single(e => e.Player == K("A"));
        var bEntry = snapshot.Single(e => e.Player == K("B"));
        var cEntry = snapshot.Single(e => e.Player == K("C"));
        Assert.Equal(partyId, aEntry.Group);
        Assert.Equal(partyId, bEntry.Group);
        Assert.Null(cEntry.Group);
    }

    [Fact]
    public void CancelMatchAsAfk_skips_readied_player_who_is_in_a_pending_timeout()
    {
        // Defensive: if a readied player picks up a penalty between match-form and AFK-cancel
        // (an extreme edge but cheap to guard), they're skipped on the auto-re-enqueue and no
        // OnQueueAdded fires for them.
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        foreach (var n in new[] { "A", "B", "C", "D" }) h.Enqueue(n);
        h.Engine.Tick(T0);

        var matchId = h.Engine.ActiveMatches.Keys.Single();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        foreach (var n in new[] { "A", "B", "C", "D" })
            h.Engine.OnPlayerJoinedArena(K(n), h.Clock.UtcNow);

        // Apply a penalty to one of the readied players so CheckEligibility flips to InTimeout.
        h.Engine.Penalties.RecordPenalty(K("A"), PenaltyKind.Abandonment, h.Clock.UtcNow);

        h.Clock.Advance(TimeSpan.FromSeconds(10));
        int queueAddsBefore = h.Telemetry.QueueAdds.Count;
        Assert.True(h.Engine.CancelMatchAsAfk(matchId, new[] { K("D") }, h.Clock.UtcNow));

        h.Engine.Queues.TryGet("2v2", out var def);
        Assert.False(def!.Queue.Contains(K("A")));   // skipped due to timeout
        Assert.True(def.Queue.Contains(K("B")));
        Assert.True(def.Queue.Contains(K("C")));
        Assert.False(def.Queue.Contains(K("D")));

        // Two re-enqueue events fired (B and C); the ineligible A was silently skipped.
        Assert.Equal(2, h.Telemetry.QueueAdds.Count - queueAddsBefore);
    }

    [Fact]
    public void Constructor_validates_required_arguments()
    {
        var clock = new FakeClock();
        var ratings = new InMemoryRatingStore();
        var policies = new[] { PenaltyPolicy.DefaultAbandonment };

        Assert.Throws<ArgumentNullException>(() => new MatchmakingEngine(null!, clock, policies));
        Assert.Throws<ArgumentNullException>(() => new MatchmakingEngine(ratings, null!, policies));
        Assert.Throws<ArgumentNullException>(() => new MatchmakingEngine(ratings, clock, null!));
    }
}
