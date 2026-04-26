using ClashEngine.Core;
using ClashEngine.Core.Eligibility;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests;

public class VetoFlowTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new(T0);
        public InMemoryRatingStore Ratings { get; } = new();
        public RecordingTelemetry Telemetry { get; } = new();
        public MatchmakingEngine Engine { get; }

        public Harness(int teamkillThreshold = 2, int vetoesRequired = 2, TimeSpan? vetoWindow = null)
        {
            Engine = new MatchmakingEngine(
                Ratings, Clock,
                new[] { PenaltyPolicy.DefaultAbandonment, PenaltyPolicy.DefaultGriefing },
                quality: new OrdinalSpreadQuality(),
                telemetry: Telemetry,
                joinTimeout: TimeSpan.FromMinutes(1),
                graceWindow: TimeSpan.FromSeconds(30));

            Engine.Queues.Register(
                "4v4",
                new MatchShape(2, 4),
                new PartitionQualityPolicy(0.5, 0.15, TimeSpan.FromSeconds(90)),
                new GameTypeId(1),
                () => new KillCountEndPolicy(3),
                () => new TeamkillThresholdHeuristic(teamkillThreshold),
                vetoesRequired,
                vetoWindow ?? TimeSpan.FromMinutes(1));
        }

        public ActiveMatch StartMatch()
        {
            string[] names = { "A", "B", "C", "D", "E", "F", "G", "H" };
            foreach (var n in names) Engine.OnPlayerConnected(K(n), Clock.UtcNow);
            foreach (var n in names) Engine.TryEnqueue(K(n), "4v4", Clock.UtcNow);
            Engine.Tick(Clock.UtcNow);

            Clock.Advance(TimeSpan.FromSeconds(2));
            foreach (var n in names) Engine.OnPlayerJoinedArena(K(n), Clock.UtcNow);

            return Telemetry.Started[0];
        }

        public void Teamkill(ActiveMatch match, int times = 1)
        {
            // Have team-0 player 0 teamkill team-0 player 1.
            var killer = match.Teams[0][0];
            var victim = match.Teams[0][1];
            for (int i = 0; i < times; i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                Engine.OnKill(killer, victim, Clock.UtcNow);
            }
        }

        public void EndMatchByKills(ActiveMatch match, int kills = 3)
        {
            var killer = match.Teams[1][0];
            var victim = match.Teams[0][0];
            for (int i = 0; i < kills; i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                Engine.OnKill(killer, victim, Clock.UtcNow);
            }
        }
    }

    [Fact]
    public void Heuristic_does_not_flag_below_threshold()
    {
        var h = new Harness(teamkillThreshold: 5);
        var m = h.StartMatch();
        h.Teamkill(m, times: 3);
        h.EndMatchByKills(m, kills: 3);

        Assert.Empty(h.Telemetry.GriefingFlagged);
    }

    [Fact]
    public void Heuristic_flag_records_penalty_and_emits_pending()
    {
        var h = new Harness(teamkillThreshold: 2);
        var m = h.StartMatch();
        h.Teamkill(m, times: 3);
        h.EndMatchByKills(m, kills: 3);

        Assert.Single(h.Telemetry.GriefingFlagged);
        var pending = h.Telemetry.GriefingFlagged[0];
        Assert.Equal(m.Teams[0][0], pending.Target);
        Assert.True(h.Engine.Penalties.IsInTimeout(pending.Target, h.Clock.UtcNow));
    }

    [Fact]
    public void Eligible_voters_are_match_participants_excluding_target()
    {
        var h = new Harness(teamkillThreshold: 2, vetoesRequired: 2);
        var m = h.StartMatch();
        h.Teamkill(m, times: 3);
        h.EndMatchByKills(m, kills: 3);

        var pending = h.Telemetry.GriefingFlagged[0];
        Assert.Equal(7, pending.EligibleVoters.Count);  // 8 participants minus target
        Assert.DoesNotContain(pending.Target, pending.EligibleVoters);
    }

    [Fact]
    public void Veto_below_threshold_returns_RecordedNeedMore()
    {
        var h = new Harness(teamkillThreshold: 2, vetoesRequired: 3);
        var m = h.StartMatch();
        h.Teamkill(m, times: 3);
        h.EndMatchByKills(m, kills: 3);

        var pending = h.Telemetry.GriefingFlagged[0];
        var voter1 = pending.EligibleVoters.First();

        var result = h.Engine.Veto(pending.MatchId, pending.Target, voter1, h.Clock.UtcNow);

        Assert.Equal(VetoResult.RecordedNeedMore, result);
        Assert.Single(h.Telemetry.VetoesRecorded);
        Assert.True(h.Engine.Penalties.IsInTimeout(pending.Target, h.Clock.UtcNow));
    }

    [Fact]
    public void Veto_threshold_rescinds_penalty_and_clears_pending()
    {
        var h = new Harness(teamkillThreshold: 2, vetoesRequired: 2);
        var m = h.StartMatch();
        h.Teamkill(m, times: 3);
        h.EndMatchByKills(m, kills: 3);

        var pending = h.Telemetry.GriefingFlagged[0];
        var voters = pending.EligibleVoters.Take(2).ToList();

        Assert.Equal(VetoResult.RecordedNeedMore, h.Engine.Veto(pending.MatchId, pending.Target, voters[0], h.Clock.UtcNow));
        Assert.Equal(VetoResult.PenaltyRescinded, h.Engine.Veto(pending.MatchId, pending.Target, voters[1], h.Clock.UtcNow));

        Assert.Empty(h.Engine.PendingGriefingPenalties);
        Assert.False(h.Engine.Penalties.IsInTimeout(pending.Target, h.Clock.UtcNow));
        Assert.Single(h.Telemetry.GriefingVetoed);
    }

    [Fact]
    public void Voting_twice_returns_AlreadyVoted()
    {
        var h = new Harness(teamkillThreshold: 2, vetoesRequired: 3);
        var m = h.StartMatch();
        h.Teamkill(m, times: 3);
        h.EndMatchByKills(m, kills: 3);

        var pending = h.Telemetry.GriefingFlagged[0];
        var voter = pending.EligibleVoters.First();

        Assert.Equal(VetoResult.RecordedNeedMore, h.Engine.Veto(pending.MatchId, pending.Target, voter, h.Clock.UtcNow));
        Assert.Equal(VetoResult.AlreadyVoted, h.Engine.Veto(pending.MatchId, pending.Target, voter, h.Clock.UtcNow));
    }

    [Fact]
    public void Target_voting_against_themselves_is_not_eligible()
    {
        var h = new Harness(teamkillThreshold: 2, vetoesRequired: 2);
        var m = h.StartMatch();
        h.Teamkill(m, times: 3);
        h.EndMatchByKills(m, kills: 3);

        var pending = h.Telemetry.GriefingFlagged[0];

        Assert.Equal(VetoResult.NotEligible,
            h.Engine.Veto(pending.MatchId, pending.Target, pending.Target, h.Clock.UtcNow));
    }

    [Fact]
    public void Non_participant_voting_is_not_eligible()
    {
        var h = new Harness(teamkillThreshold: 2, vetoesRequired: 2);
        var m = h.StartMatch();
        h.Teamkill(m, times: 3);
        h.EndMatchByKills(m, kills: 3);

        var pending = h.Telemetry.GriefingFlagged[0];
        Assert.Equal(VetoResult.NotEligible,
            h.Engine.Veto(pending.MatchId, pending.Target, K("Outsider"), h.Clock.UtcNow));
    }

    [Fact]
    public void Veto_for_unknown_match_returns_NoPendingPenalty()
    {
        var h = new Harness();
        Assert.Equal(VetoResult.NoPendingPenalty,
            h.Engine.Veto(Guid.NewGuid(), K("A"), K("B"), h.Clock.UtcNow));
    }

    [Fact]
    public void Window_expiry_confirms_penalty_and_emits_telemetry()
    {
        var h = new Harness(teamkillThreshold: 2, vetoesRequired: 2, vetoWindow: TimeSpan.FromSeconds(60));
        var m = h.StartMatch();
        h.Teamkill(m, times: 3);
        h.EndMatchByKills(m, kills: 3);

        var pending = h.Telemetry.GriefingFlagged[0];

        // Advance past window and tick to trigger expiry handling.
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        h.Engine.Tick(h.Clock.UtcNow);

        Assert.Empty(h.Engine.PendingGriefingPenalties);
        Assert.Single(h.Telemetry.GriefingConfirmed);
        // Penalty itself should still be in tracker (the timeout has expired naturally by now though).
    }

    [Fact]
    public void Veto_after_expiry_returns_WindowExpired()
    {
        var h = new Harness(teamkillThreshold: 2, vetoesRequired: 2, vetoWindow: TimeSpan.FromSeconds(30));
        var m = h.StartMatch();
        h.Teamkill(m, times: 3);
        h.EndMatchByKills(m, kills: 3);

        var pending = h.Telemetry.GriefingFlagged[0];
        h.Clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(VetoResult.WindowExpired,
            h.Engine.Veto(pending.MatchId, pending.Target, pending.EligibleVoters.First(), h.Clock.UtcNow));
    }

    [Fact]
    public void Cancelled_match_does_not_run_griefing_heuristic()
    {
        var h = new Harness(teamkillThreshold: 2);
        // Force a cancellation: only a couple players join, then time out.
        string[] names = { "A", "B", "C", "D", "E", "F", "G", "H" };
        foreach (var n in names) h.Engine.OnPlayerConnected(K(n), h.Clock.UtcNow);
        foreach (var n in names) h.Engine.TryEnqueue(K(n), "4v4", h.Clock.UtcNow);
        h.Engine.Tick(h.Clock.UtcNow);

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        h.Engine.Tick(h.Clock.UtcNow);

        Assert.Equal(MatchState.Cancelled, h.Telemetry.Ended[0].FinalState);
        Assert.Empty(h.Telemetry.GriefingFlagged);
    }

    [Fact]
    public void Rescinded_griefing_penalty_lets_player_queue_again()
    {
        var h = new Harness(teamkillThreshold: 2, vetoesRequired: 2);
        var m = h.StartMatch();
        h.Teamkill(m, times: 3);
        h.EndMatchByKills(m, kills: 3);

        var pending = h.Telemetry.GriefingFlagged[0];
        var voters = pending.EligibleVoters.Take(2).ToList();
        h.Engine.Veto(pending.MatchId, pending.Target, voters[0], h.Clock.UtcNow);
        h.Engine.Veto(pending.MatchId, pending.Target, voters[1], h.Clock.UtcNow);

        // Target should be Available now.
        Assert.Equal(EligibilityStatus.Available, h.Engine.CheckEligibility(pending.Target).Status);
    }
}
