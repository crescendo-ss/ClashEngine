using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Eligibility;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests;

/// <summary>
/// <see cref="MatchmakingEngine.TryFormMatch"/>: the external-roster seam for private
/// matchmaking. A formed match must run the ordinary lifecycle (proposed -> live -> ended) while
/// never touching the anchor queue's membership beyond sweeping/restoring the roster's own spots.
/// </summary>
public class FormMatchTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public InMemoryRatingStore Ratings { get; } = new();
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }

        public Harness(int killTarget = 1, bool kothQueue = false)
        {
            Engine = new MatchmakingEngine(
                Ratings, Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                quality: new OrdinalSpreadQuality(),
                telemetry: Telemetry,
                joinTimeout: TimeSpan.FromMinutes(1),
                graceWindow: TimeSpan.FromSeconds(30));

            Engine.Queues.Register(
                "2v2",
                new MatchShape(2, 2),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                "gt1",
                () => new KillCountEndPolicy(killTarget),
                promoteWinnersToFront: kothQueue);
        }

        public void Connect(params string[] names)
        {
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
        }

        /// <summary>Drives every rostered player to Active and marks the match live, the same
        /// sequence the orchestrator performs at GO!.</summary>
        public void GoLive(Guid matchId)
        {
            var match = Engine.ActiveMatches[matchId];
            foreach (var team in match.Teams)
                foreach (var p in team)
                    Engine.OnPlayerJoinedArena(p, Clock.UtcNow);
            Assert.True(Engine.MarkMatchLive(matchId, Clock.UtcNow));
        }
    }

    private static IReadOnlyList<IReadOnlyList<PlayerKey>> Teams2v2(string a, string b, string c, string d) =>
        new[] { new[] { K(a), K(b) }, new[] { K(c), K(d) } };

    [Fact]
    public void Form_match_creates_forming_match_and_fires_proposed()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");

        var outcome = h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "D"), T0);

        Assert.Equal(FormMatchResult.Ok, outcome.Result);
        Assert.NotEqual(Guid.Empty, outcome.MatchId);
        Assert.Single(h.Telemetry.Proposed);
        Assert.Equal("2v2", h.Telemetry.Proposed[0].QueueName);
        Assert.Equal(1.0, h.Telemetry.Proposed[0].Quality);

        var match = h.Engine.ActiveMatches[outcome.MatchId];
        Assert.Equal(MatchState.Forming, match.State);
        // The orchestrator registry correlates proposal -> match id by Teams reference identity;
        // the injected path must preserve it exactly like a matcher pop does.
        Assert.Same(h.Telemetry.Proposed[0].Teams, match.Teams);

        foreach (var n in new[] { "A", "B", "C", "D" })
            Assert.Equal(EligibilityStatus.InMatch, h.Engine.CheckEligibility(K(n)).Status);
    }

    [Fact]
    public void Formed_match_runs_the_ordinary_lifecycle_to_completion()
    {
        var h = new Harness(killTarget: 1);
        h.Connect("A", "B", "C", "D");
        var outcome = h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "D"), T0);

        h.GoLive(outcome.MatchId);
        Assert.Single(h.Telemetry.Started);

        h.Engine.OnKill(K("A"), K("C"), h.Clock.UtcNow);

        Assert.Single(h.Telemetry.Ended);
        Assert.Equal(MatchState.Completed, h.Telemetry.Ended[0].FinalState);
        Assert.False(h.Engine.ActiveMatches.ContainsKey(outcome.MatchId));
    }

    [Fact]
    public void Rejects_unknown_queue()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        var outcome = h.Engine.TryFormMatch("nope", Teams2v2("A", "B", "C", "D"), T0);
        Assert.Equal(FormMatchResult.UnknownQueue, outcome.Result);
        Assert.Empty(h.Engine.ActiveMatches);
    }

    [Fact]
    public void Rejects_wrong_team_count_and_wrong_team_size()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D", "E", "F");

        var threeTeams = new[] { new[] { K("A"), K("B") }, new[] { K("C"), K("D") }, new[] { K("E"), K("F") } };
        Assert.Equal(FormMatchResult.ShapeMismatch, h.Engine.TryFormMatch("2v2", threeTeams, T0).Result);

        var lopsided = new[] { new[] { K("A"), K("B"), K("E") }, new[] { K("C"), K("D") } };
        Assert.Equal(FormMatchResult.ShapeMismatch, h.Engine.TryFormMatch("2v2", lopsided, T0).Result);

        Assert.Empty(h.Engine.ActiveMatches);
    }

    [Fact]
    public void Rejects_duplicate_player_naming_the_offender()
    {
        var h = new Harness();
        h.Connect("A", "B", "C");
        var outcome = h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "A"), T0);
        Assert.Equal(FormMatchResult.DuplicatePlayer, outcome.Result);
        Assert.Equal(K("A"), outcome.Player);
    }

    [Fact]
    public void Rejects_disconnected_player_naming_the_offender()
    {
        var h = new Harness();
        h.Connect("A", "B", "C");   // D never connects
        var outcome = h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "D"), T0);
        Assert.Equal(FormMatchResult.NotConnected, outcome.Result);
        Assert.Equal(K("D"), outcome.Player);
        Assert.Empty(h.Engine.ActiveMatches);
    }

    [Fact]
    public void Rejects_player_already_in_a_match()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D", "E", "F", "G");
        Assert.Equal(FormMatchResult.Ok, h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "D"), T0).Result);

        var outcome = h.Engine.TryFormMatch("2v2", Teams2v2("E", "F", "G", "A"), T0);
        Assert.Equal(FormMatchResult.InMatch, outcome.Result);
        Assert.Equal(K("A"), outcome.Player);
        Assert.Single(h.Engine.ActiveMatches);
    }

    [Fact]
    public void Rejects_player_serving_a_timeout()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        h.Engine.Penalties.RecordPenalty(K("D"), PenaltyKind.Abandonment, T0);

        var outcome = h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "D"), T0);
        Assert.Equal(FormMatchResult.InTimeout, outcome.Result);
        Assert.Equal(K("D"), outcome.Player);
    }

    [Fact]
    public void Sweeps_queued_roster_players_and_restores_them_when_the_match_ends()
    {
        var h = new Harness(killTarget: 1);
        h.Connect("A", "B", "C", "D");
        Assert.Equal(EnqueueResult.Ok, h.Engine.TryEnqueue(K("A"), "2v2", T0));

        var outcome = h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "D"), T0);
        Assert.Equal(FormMatchResult.Ok, outcome.Result);

        // A's waiting spot was swept (reason Matched) -- the queue board must reflect the drop.
        Assert.Empty(h.Engine.QueuesFor(K("A")));
        Assert.Contains(h.Telemetry.QueueRemovals,
            r => r.Player.Equals(K("A")) && r.Queue == "2v2" && r.Reason == QueueRemovalReason.Matched);

        h.GoLive(outcome.MatchId);
        h.Engine.OnKill(K("A"), K("C"), h.Clock.UtcNow);

        // The private match consumed no queue spot, so A's explicit enqueue comes back; B (who
        // never queued) does not.
        Assert.Contains(h.Telemetry.QueueRestored, r => r.Player.Equals(K("A")) && r.Queue == "2v2");
        Assert.Equal(new[] { "2v2" }, h.Engine.QueuesFor(K("A")));
        Assert.Empty(h.Engine.QueuesFor(K("B")));
    }

    [Fact]
    public void Completed_external_match_skips_koth_promotion_and_auto_requeue()
    {
        var h = new Harness(killTarget: 1, kothQueue: true);
        h.Connect("A", "B", "C", "D");
        h.Engine.AutoQueue.Set(K("B"), true);

        var outcome = h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "D"), T0);
        h.GoLive(outcome.MatchId);
        h.Engine.OnKill(K("A"), K("C"), h.Clock.UtcNow);

        Assert.Single(h.Telemetry.Ended);
        // Winners are NOT promoted into the anchor queue's head, and ?auto players are NOT
        // re-added -- the roster never held a spot in that public line.
        Assert.Empty(h.Telemetry.WinnerPromotions);
        Assert.Empty(h.Telemetry.AutoQueued);
        foreach (var n in new[] { "A", "B", "C", "D" })
            Assert.Empty(h.Engine.QueuesFor(K(n)));
    }

    [Fact]
    public void Afk_cancelled_external_match_does_not_requeue_readied_players()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        var outcome = h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "D"), T0);

        Assert.True(h.Engine.CancelMatchAsAfk(outcome.MatchId, new[] { K("A") }, h.Clock.UtcNow));

        Assert.Single(h.Telemetry.Ended);
        Assert.Equal(MatchState.Cancelled, h.Telemetry.Ended[0].FinalState);
        // Matcher-formed AFK cancels re-queue the readied players at the queue's head; an
        // external roster never queued, so nobody gets inserted.
        Assert.Empty(h.Telemetry.QueueAdds);
        foreach (var n in new[] { "B", "C", "D" })
            Assert.Empty(h.Engine.QueuesFor(K(n)));
    }

    [Fact]
    public void Blameless_cancel_voids_the_match_without_penalties()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        var outcome = h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "D"), T0);

        // The organizer disbanding the lobby is not a readiness failure: nobody is flagged, so
        // everyone may immediately queue or form again.
        Assert.True(h.Engine.CancelForming(outcome.MatchId, h.Clock.UtcNow, blameless: true));
        Assert.False(h.Engine.ActiveMatches.ContainsKey(outcome.MatchId));
        Assert.Single(h.Telemetry.Ended);
        Assert.Empty(h.Telemetry.Ended[0].AbandonedBy);
        foreach (var n in new[] { "A", "B", "C", "D" })
            Assert.Equal(EligibilityStatus.Available, h.Engine.CheckEligibility(K(n)).Status);
    }

    [Fact]
    public void Default_cancel_forming_still_flags_no_shows()
    {
        var h = new Harness();
        h.Connect("A", "B", "C", "D");
        var outcome = h.Engine.TryFormMatch("2v2", Teams2v2("A", "B", "C", "D"), T0);

        // The pre-existing semantics are unchanged: an orchestrator/join-timeout style cancel
        // treats never-joined players as no-shows and penalizes them.
        Assert.True(h.Engine.CancelForming(outcome.MatchId, h.Clock.UtcNow));
        Assert.Single(h.Telemetry.Ended);
        Assert.Equal(4, h.Telemetry.Ended[0].AbandonedBy.Count);
        Assert.Equal(EligibilityStatus.InTimeout, h.Engine.CheckEligibility(K("A")).Status);
    }
}
