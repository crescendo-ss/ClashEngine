using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests;

public class KothModeTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public InMemoryRatingStore Ratings { get; } = new();
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }

        public Harness(int killTarget = 1, int maxDefenses = 3)
        {
            Engine = new MatchmakingEngine(
                Ratings, Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                quality: new OrdinalSpreadQuality(),
                telemetry: Telemetry,
                joinTimeout: TimeSpan.FromMinutes(1),
                graceWindow: TimeSpan.FromSeconds(30));

            Engine.Queues.Register(
                "koth2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1",
                () => new KillCountEndPolicy(killTarget),
                vetoesRequired: 1,
                promoteWinnersToFront: true,
                maxConsecutiveDefenses: maxDefenses);
        }

        public void ConnectAll(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
        }

        /// <summary>Drives the most recently proposed match through join + GO!.</summary>
        public ActiveMatch StartProposedMatch(MatchProposal proposal)
        {
            foreach (var team in proposal.Teams)
                foreach (var p in team)
                    Engine.OnPlayerJoinedArena(p, Clock.UtcNow);
            var matchId = Engine.ActiveMatches
                .First(kvp => ReferenceEquals(kvp.Value.Teams, proposal.Teams)).Key;
            Engine.MarkMatchLive(matchId, Clock.UtcNow);
            return Engine.ActiveMatches[matchId];
        }

        public ActiveMatch RunMatchAndPickWinningTeam(string winningKiller, string victim)
        {
            Engine.Tick(Clock.UtcNow);
            var match = Telemetry.Started.Last();
            Clock.Advance(TimeSpan.FromSeconds(1));
            Engine.OnKill(K(winningKiller), K(victim), Clock.UtcNow);
            return match;
        }
    }

    [Fact]
    public void Winners_are_re_enqueued_at_the_head_of_a_KOTH_queue()
    {
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");

        // Round 1: enqueue A, B, C, D and pop a match.
        h.Engine.TryEnqueue(K("A"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("B"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("C"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("D"), "koth2v2", T0);
        h.Engine.Tick(T0);

        var proposal = h.Telemetry.Proposed[0];
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.StartProposedMatch(proposal);
        var match = h.Telemetry.Started[0];

        var winners = match.Teams[0];   // pick team 0 to win
        var loser = match.Teams[1][0];

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(winners[0], loser, h.Clock.UtcNow);

        // After match end: winners should be in the queue (re-enqueued at the head).
        h.Engine.Queues.TryGet("koth2v2", out var def);
        var snap = def!.Queue.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Contains(snap, e => e.Player == winners[0]);
        Assert.Contains(snap, e => e.Player == winners[1]);
        // The losers should NOT be re-enqueued.
        Assert.DoesNotContain(snap, e => e.Player == loser);
    }

    [Fact]
    public void Champions_at_defense_cap_are_sent_to_back()
    {
        var h = new Harness(killTarget: 1, maxDefenses: 1);
        h.ConnectAll("A", "B", "C", "D", "E", "F");

        // Match 1: A, B, C, D pop. Team 0 wins.
        h.Engine.TryEnqueue(K("A"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("B"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("C"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("D"), "koth2v2", T0);
        h.Engine.Tick(T0);
        var p1 = h.Telemetry.Proposed[0];
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.StartProposedMatch(p1);

        var winners1 = p1.Teams[0];
        var loser1 = p1.Teams[1][0];
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(winners1[0], loser1, h.Clock.UtcNow);

        // Now winners are back at the head with defense count = 1.
        // Add E, F to the queue; another match pops with the priority winners on top.
        h.Engine.TryEnqueue(K("E"), "koth2v2", h.Clock.UtcNow);
        h.Engine.TryEnqueue(K("F"), "koth2v2", h.Clock.UtcNow);
        h.Engine.Tick(h.Clock.UtcNow);

        var p2 = h.Telemetry.Proposed[1];
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.StartProposedMatch(p2);

        // Have the same winners win again — at defense cap = 1, this is their last "defense."
        var winners2 = p2.Teams.First(t => t.Contains(winners1[0]));
        var loser2 = p2.Teams.First(t => !t.Contains(winners1[0]))[0];
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(winners2[0], loser2, h.Clock.UtcNow);

        // After match 2 end: champions hit the defense cap. They are still re-enqueued, but
        // their consecutive counter is reset and they go to the tail (proving cap behavior
        // requires a third pop with new challengers in front, which we skip here).
        h.Engine.Queues.TryGet("koth2v2", out var def);
        var snap = def!.Queue.Snapshot();
        Assert.Equal(2, snap.Count);   // both winners re-enqueued
        foreach (var p in winners2)
            Assert.Contains(snap, e => e.Player == p);
    }

    [Fact]
    public void Promoted_winners_fire_OnWinnerPromoted_not_OnQueueAdded()
    {
        var h = new Harness(killTarget: 1, maxDefenses: 3);
        h.ConnectAll("A", "B", "C", "D");

        h.Engine.TryEnqueue(K("A"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("B"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("C"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("D"), "koth2v2", T0);
        h.Engine.Tick(T0);

        var proposal = h.Telemetry.Proposed[0];
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.StartProposedMatch(proposal);
        var match = h.Telemetry.Started[0];
        var winners = match.Teams[0];
        var loser = match.Teams[1][0];

        // Snapshot the OnQueueAdded count from the initial enqueues; KOTH re-enqueue must not
        // emit any further OnQueueAdded events.
        var queueAddsBefore = h.Telemetry.QueueAdds.Count;

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(winners[0], loser, h.Clock.UtcNow);

        Assert.Equal(queueAddsBefore, h.Telemetry.QueueAdds.Count);
        Assert.Equal(2, h.Telemetry.WinnerPromotions.Count);
        foreach (var w in winners)
        {
            var promo = h.Telemetry.WinnerPromotions.Single(p => p.Player == w);
            Assert.Equal("koth2v2", promo.Queue);
            Assert.Equal(1, promo.DefensesUsed);
            Assert.Equal(3, promo.MaxDefenses);
            Assert.False(promo.SentToBack);
        }
    }

    [Fact]
    public void Capped_champions_fire_OnWinnerPromoted_with_SentToBack()
    {
        var h = new Harness(killTarget: 1, maxDefenses: 1);
        h.ConnectAll("A", "B", "C", "D");

        h.Engine.TryEnqueue(K("A"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("B"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("C"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("D"), "koth2v2", T0);
        h.Engine.Tick(T0);
        var p1 = h.Telemetry.Proposed[0];
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.StartProposedMatch(p1);
        var winners1 = p1.Teams[0];
        var loser1 = p1.Teams[1][0];
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(winners1[0], loser1, h.Clock.UtcNow);

        // Match 1 promotion: head-of-queue, defense 1/1.
        Assert.Equal(2, h.Telemetry.WinnerPromotions.Count);
        Assert.All(h.Telemetry.WinnerPromotions, e => Assert.False(e.SentToBack));

        h.Telemetry.WinnerPromotions.Clear();

        // Match 2 needs four players to pop -- bring in fresh challengers for the cap to bite.
        h.Engine.OnPlayerConnected(K("E"), h.Clock.UtcNow);
        h.Engine.OnPlayerConnected(K("F"), h.Clock.UtcNow);
        h.Engine.TryEnqueue(K("E"), "koth2v2", h.Clock.UtcNow);
        h.Engine.TryEnqueue(K("F"), "koth2v2", h.Clock.UtcNow);
        h.Engine.Tick(h.Clock.UtcNow);
        var p2 = h.Telemetry.Proposed[1];
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.StartProposedMatch(p2);
        var winners2 = p2.Teams.First(t => t.Contains(winners1[0]));
        var loser2 = p2.Teams.First(t => !t.Contains(winners1[0]))[0];
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(winners2[0], loser2, h.Clock.UtcNow);

        // Match 2 promotion: cap hit -- both winners reported with SentToBack=true and DefensesUsed=0.
        Assert.Equal(2, h.Telemetry.WinnerPromotions.Count);
        foreach (var w in winners2)
        {
            var promo = h.Telemetry.WinnerPromotions.Single(e => e.Player == w);
            Assert.True(promo.SentToBack);
            Assert.Equal(0, promo.DefensesUsed);
            Assert.Equal(1, promo.MaxDefenses);
        }
    }

    [Fact]
    public void OnPlayerSpecced_after_KOTH_promotion_keeps_winners_in_the_queue()
    {
        // Repro: the orchestrator specs each match participant during its OnMatchEnded cleanup
        // (SetShipAndFreq -> arena spec freq), which in production fires through
        // PlayerStateObserver.OnShipFreqChange. Before the fix that path called
        // OnPlayerLeftArena, whose DequeueEverywhere yanked the freshly-promoted winners back
        // out of the queue right after ApplyKothReenqueue put them in -- and AFTER the
        // OnWinnerPromoted DM had already been buffered. Players got a "Congrats! Autoqueued
        // and promoted to be at the front..." DM but were nowhere to be found in the queue.
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");

        h.Engine.TryEnqueue(K("A"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("B"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("C"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("D"), "koth2v2", T0);
        h.Engine.Tick(T0);

        var proposal = h.Telemetry.Proposed[0];
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.StartProposedMatch(proposal);
        var match = h.Telemetry.Started[0];
        var winners = match.Teams[0];
        var loser = match.Teams[1][0];

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(winners[0], loser, h.Clock.UtcNow);

        // ApplyKothReenqueue ran during FinalizeMatch -- both winners are at the head.
        h.Engine.Queues.TryGet("koth2v2", out var def);
        Assert.Equal(2, def!.Queue.Snapshot().Count);

        // Now simulate the orchestrator's post-match spec for every match participant. In
        // production this comes through PlayerStateObserver.OnShipFreqChange -> the engine.
        // Both winners and losers get specced; only the winners are still in the queue.
        for (int t = 0; t < match.Teams.Count; t++)
            for (int j = 0; j < match.Teams[t].Count; j++)
                h.Engine.OnPlayerSpecced(match.Teams[t][j], h.Clock.UtcNow);

        // Winners must still be in the queue after the cleanup-spec wave -- otherwise the
        // OnWinnerPromoted DM that just went out is lying to them.
        var snap = def.Queue.Snapshot();
        Assert.Equal(2, snap.Count);
        foreach (var w in winners)
            Assert.Contains(snap, e => e.Player == w);
    }

    [Fact]
    public void OnPlayerLeftArena_after_KOTH_promotion_preserves_winners_in_queue()
    {
        // OnPlayerLeftArena no longer self-dequeues -- queue membership persists across any
        // arena change. The fix originally surfaced in the KOTH path (winners briefly visible
        // in the queue after promotion, then yanked by a leave-arena side effect on the
        // cleanup spec wave); the new policy makes the engine event safe to call from any
        // arena-change path.
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A", "B", "C", "D");
        h.Engine.TryEnqueue(K("A"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("B"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("C"), "koth2v2", T0);
        h.Engine.TryEnqueue(K("D"), "koth2v2", T0);
        h.Engine.Tick(T0);

        var proposal = h.Telemetry.Proposed[0];
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.StartProposedMatch(proposal);
        var match = h.Telemetry.Started[0];
        var winners = match.Teams[0];
        var loser = match.Teams[1][0];

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(winners[0], loser, h.Clock.UtcNow);

        for (int t = 0; t < match.Teams.Count; t++)
            for (int j = 0; j < match.Teams[t].Count; j++)
                h.Engine.OnPlayerLeftArena(match.Teams[t][j], h.Clock.UtcNow);

        h.Engine.Queues.TryGet("koth2v2", out var def);
        var snap = def!.Queue.Snapshot();
        foreach (var w in winners)
            Assert.Contains(snap, e => e.Player == w);
    }

    [Fact]
    public void OnPlayerLeftArena_keeps_a_queued_player_in_the_queue()
    {
        // Queue membership persists across arena changes -- only ?cancel, disconnect, match
        // formation, and ineligibility changes drop a queue entry. A player who ?go's elsewhere
        // and back keeps their spot.
        var h = new Harness(killTarget: 1);
        h.ConnectAll("A");
        h.Engine.TryEnqueue(K("A"), "koth2v2", T0);

        h.Engine.Queues.TryGet("koth2v2", out var def);
        Assert.Single(def!.Queue.Snapshot());

        h.Engine.OnPlayerLeftArena(K("A"), T0);

        Assert.Single(def.Queue.Snapshot());
    }

    [Fact]
    public void Non_KOTH_queue_does_not_re_enqueue_winners()
    {
        var h = new Harness(killTarget: 1);
        h.Engine.Queues.Register(
            "regular2v2",
            new MatchShape(2, 2),
            new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
            "gt2",
            () => new KillCountEndPolicy(1));

        h.ConnectAll("A", "B", "C", "D");
        h.Engine.TryEnqueue(K("A"), "regular2v2", T0);
        h.Engine.TryEnqueue(K("B"), "regular2v2", T0);
        h.Engine.TryEnqueue(K("C"), "regular2v2", T0);
        h.Engine.TryEnqueue(K("D"), "regular2v2", T0);
        h.Engine.Tick(T0);

        var proposal = h.Telemetry.Proposed[0];
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.StartProposedMatch(proposal);

        var winners = proposal.Teams[0];
        var loser = proposal.Teams[1][0];
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        h.Engine.OnKill(winners[0], loser, h.Clock.UtcNow);

        // Regular queue: no auto re-enqueue.
        h.Engine.Queues.TryGet("regular2v2", out var def);
        Assert.Empty(def!.Queue.Snapshot());
    }
}
