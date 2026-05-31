using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Tests.Matches;

public class ActiveMatchTests
{
    private static PlayerKey K(string name) => new(name);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ActiveMatch BuildMatch(
        IMatchEndPolicy? endPolicy = null,
        TimeSpan? joinTimeout = null,
        TimeSpan? graceWindow = null,
        IReadOnlyList<IReadOnlyList<PlayerKey>>? teams = null,
        int? livesPerPlayer = null)
    {
        teams ??= new IReadOnlyList<PlayerKey>[]
        {
            new[] { K("A"), K("B") },
            new[] { K("C"), K("D") },
        };
        return new ActiveMatch(
            Guid.NewGuid(),
            "gt1",
            teams,
            endPolicy ?? new KillCountEndPolicy(targetKills: 5),
            joinTimeout ?? TimeSpan.FromMinutes(1),
            graceWindow ?? TimeSpan.FromSeconds(30),
            T0,
            livesPerPlayer);
    }

    [Fact]
    public void New_match_is_Forming_with_all_pending()
    {
        var m = BuildMatch();
        Assert.Equal(MatchState.Forming, m.State);
        foreach (var t in m.Teams)
            foreach (var p in t)
                Assert.Equal(PlayerStatus.Pending, m.GetStatus(p));
    }

    [Fact]
    public void OnPlayerJoined_marks_pending_active_but_does_not_flip_to_Live()
    {
        // Engine state stays Forming through Setup, Staging, and Countdown -- only the
        // orchestrator's GO! call (MarkLive) flips to Live. This prevents Live-only state
        // (kill processing, team-collapse, etc.) from firing pre-GO.
        var m = BuildMatch();
        m.OnPlayerJoined(K("A"), T0.AddSeconds(1));
        m.OnPlayerJoined(K("B"), T0.AddSeconds(2));
        m.OnPlayerJoined(K("C"), T0.AddSeconds(3));
        m.OnPlayerJoined(K("D"), T0.AddSeconds(4));

        Assert.Equal(MatchState.Forming, m.State);
        Assert.Null(m.StartedAt);
        foreach (var team in m.Teams)
            foreach (var p in team)
                Assert.Equal(PlayerStatus.Active, m.GetStatus(p));
    }

    [Fact]
    public void MarkLive_after_all_active_transitions_to_Live()
    {
        var m = BuildMatch();
        m.OnPlayerJoined(K("A"), T0.AddSeconds(1));
        m.OnPlayerJoined(K("B"), T0.AddSeconds(2));
        m.OnPlayerJoined(K("C"), T0.AddSeconds(3));
        m.OnPlayerJoined(K("D"), T0.AddSeconds(4));

        Assert.True(m.MarkLive(T0.AddSeconds(10)));
        Assert.Equal(MatchState.Live, m.State);
        Assert.Equal(T0.AddSeconds(10), m.StartedAt);
    }

    [Fact]
    public void MarkLive_returns_false_when_some_player_is_still_pending()
    {
        var m = BuildMatch();
        m.OnPlayerJoined(K("A"), T0.AddSeconds(1));
        m.OnPlayerJoined(K("B"), T0.AddSeconds(2));
        m.OnPlayerJoined(K("C"), T0.AddSeconds(3));
        // D never joined.

        Assert.False(m.MarkLive(T0.AddSeconds(10)));
        Assert.Equal(MatchState.Forming, m.State);
        Assert.Null(m.StartedAt);
    }

    [Fact]
    public void MarkLive_is_idempotent_and_returns_false_when_already_live()
    {
        var m = JoinAll(BuildMatch());
        Assert.Equal(MatchState.Live, m.State);
        var startedAt = m.StartedAt;

        Assert.False(m.MarkLive(T0.AddMinutes(1)));
        Assert.Equal(startedAt, m.StartedAt);
    }

    [Fact]
    public void Joining_unknown_player_is_no_op()
    {
        var m = BuildMatch();
        m.OnPlayerJoined(K("Ghost"), T0);
        Assert.Equal(MatchState.Forming, m.State);
    }

    [Fact]
    public void Repeat_join_is_idempotent()
    {
        var m = BuildMatch();
        m.OnPlayerJoined(K("A"), T0);
        m.OnPlayerJoined(K("A"), T0.AddSeconds(1));
        Assert.Equal(PlayerStatus.Active, m.GetStatus(K("A")));
    }

    [Fact]
    public void Tick_after_join_timeout_cancels_match_and_marks_no_shows()
    {
        var m = BuildMatch(joinTimeout: TimeSpan.FromMinutes(1));

        m.OnPlayerJoined(K("A"), T0.AddSeconds(5));
        m.OnPlayerJoined(K("B"), T0.AddSeconds(5));
        m.OnPlayerJoined(K("C"), T0.AddSeconds(5));
        // D never joins.

        m.Tick(T0.AddMinutes(1));

        Assert.Equal(MatchState.Cancelled, m.State);
        Assert.NotNull(m.Outcome);
        Assert.Equal(MatchState.Cancelled, m.Outcome!.FinalState);
        Assert.Single(m.Outcome.AbandonedBy);
        Assert.Equal(K("D"), m.Outcome.AbandonedBy[0]);
        Assert.Empty(m.Outcome.RankedTeams);
    }

    [Fact]
    public void OnPlayerLeft_during_Live_moves_player_to_grace()
    {
        var m = JoinAll(BuildMatch());
        m.OnPlayerLeft(K("A"), T0.AddSeconds(10));
        Assert.Equal(PlayerStatus.InGrace, m.GetStatus(K("A")));
    }

    [Fact]
    public void OnPlayerReturned_within_grace_restores_active()
    {
        var m = JoinAll(BuildMatch());
        m.OnPlayerLeft(K("A"), T0.AddSeconds(10));
        m.OnPlayerReturned(K("A"), T0.AddSeconds(20));
        Assert.Equal(PlayerStatus.Active, m.GetStatus(K("A")));
    }

    [Fact]
    public void Tick_after_grace_expiry_marks_player_abandoned()
    {
        var m = JoinAll(BuildMatch(graceWindow: TimeSpan.FromSeconds(30)));
        m.OnPlayerLeft(K("A"), T0.AddSeconds(10));
        m.Tick(T0.AddSeconds(45));
        Assert.Equal(PlayerStatus.Abandoned, m.GetStatus(K("A")));
    }

    [Fact]
    public void OnKill_credits_killer_team_only_for_cross_team_kill()
    {
        var m = JoinAll(BuildMatch());
        m.OnKill(K("A"), K("C"), T0.AddSeconds(10));
        Assert.Equal(1, m.KillsByTeam[0]);
        Assert.Equal(0, m.KillsByTeam[1]);
    }

    [Fact]
    public void Teamkill_does_not_score()
    {
        var m = JoinAll(BuildMatch());
        m.OnKill(K("A"), K("B"), T0.AddSeconds(10));
        Assert.Equal(0, m.KillsByTeam[0]);
    }

    [Fact]
    public void Teamkill_increments_per_player_teamkill_counter()
    {
        var m = JoinAll(BuildMatch());
        m.OnKill(K("A"), K("B"), T0.AddSeconds(10));
        m.OnKill(K("A"), K("B"), T0.AddSeconds(20));

        Assert.Equal(2, m.TeamkillsByPlayer[K("A")]);
        Assert.False(m.KillsByPlayer.ContainsKey(K("A")));
        Assert.Equal(2, m.DeathsByPlayer[K("B")]);
    }

    [Fact]
    public void Cross_team_kill_increments_killer_kills_and_victim_deaths()
    {
        var m = JoinAll(BuildMatch());
        m.OnKill(K("A"), K("C"), T0.AddSeconds(10));

        Assert.Equal(1, m.KillsByPlayer[K("A")]);
        Assert.Equal(1, m.DeathsByPlayer[K("C")]);
        Assert.False(m.TeamkillsByPlayer.ContainsKey(K("A")));
    }

    [Fact]
    public void Without_lives_no_lives_or_exits_are_tracked()
    {
        var m = JoinAll(BuildMatch(livesPerPlayer: null));
        m.OnKill(K("A"), K("C"), T0.AddSeconds(10));

        Assert.Empty(m.LivesRemaining);
        Assert.Empty(m.ExitedAt);
        Assert.Null(m.GetParticipationTime(K("A"), T0.AddMinutes(1)));
    }

    [Fact]
    public void Lives_initialize_at_configured_value_for_every_player()
    {
        // LivesRemaining counts respawns left, so a Lives=3 player has 2 remaining respawns
        // beyond the initial spawn.
        var m = BuildMatch(livesPerPlayer: 3);
        foreach (var team in m.Teams)
            foreach (var p in team)
                Assert.Equal(2, m.LivesRemaining[p]);
    }

    [Fact]
    public void Cross_team_death_decrements_victim_lives()
    {
        var m = JoinAll(BuildMatch(livesPerPlayer: 3, endPolicy: new KillCountEndPolicy(100)));
        m.OnKill(K("A"), K("C"), T0.AddSeconds(10));

        Assert.Equal(2, m.LivesRemaining[K("A")]);
        Assert.Equal(1, m.LivesRemaining[K("C")]);
    }

    [Fact]
    public void Teamkill_decrements_victim_lives()
    {
        // Aligned with CaptainsMatch / TeamVersusMatch: a TK victim loses a life just like a
        // cross-team victim does, so the statbox doesn't render the player as immortal under
        // friendly fire. Lives=3 -> initial respawns=2; after one TK, B has 1 respawn left.
        var m = JoinAll(BuildMatch(livesPerPlayer: 3));
        m.OnKill(K("A"), K("B"), T0.AddSeconds(10));

        Assert.Equal(1, m.LivesRemaining[K("B")]);
    }

    [Fact]
    public void Reaching_zero_lives_records_exit_time()
    {
        // Lives=3 -> initial respawns=2. The first two deaths consume both respawns; the third
        // death lands with no respawn left and is the eliminating one that sets ExitedAt.
        var m = JoinAll(BuildMatch(livesPerPlayer: 3, endPolicy: new KillCountEndPolicy(100)));

        m.OnKill(K("A"), K("C"), T0.AddSeconds(10));
        m.OnKill(K("A"), K("C"), T0.AddSeconds(20));
        Assert.Equal(0, m.LivesRemaining[K("C")]);
        Assert.False(m.ExitedAt.ContainsKey(K("C")));

        m.OnKill(K("A"), K("C"), T0.AddSeconds(30));
        Assert.Equal(0, m.LivesRemaining[K("C")]);
        Assert.Equal(T0.AddSeconds(30), m.ExitedAt[K("C")]);
    }

    [Fact]
    public void Exit_time_is_first_zero_only_not_overwritten_by_later_kills()
    {
        var m = JoinAll(BuildMatch(livesPerPlayer: 1, endPolicy: new KillCountEndPolicy(100)));

        m.OnKill(K("A"), K("C"), T0.AddSeconds(10));
        // C is already at 0 lives; further kills shouldn't shift the recorded exit.
        m.OnKill(K("A"), K("C"), T0.AddSeconds(20));

        Assert.Equal(T0.AddSeconds(10), m.ExitedAt[K("C")]);
    }

    [Fact]
    public void GetParticipationTime_for_alive_player_uses_now()
    {
        var startedAt = T0.AddSeconds(5);
        var m = JoinAll(BuildMatch(livesPerPlayer: 3, endPolicy: new KillCountEndPolicy(100)),
            at: startedAt);

        var now = startedAt.AddMinutes(2);
        Assert.Equal(TimeSpan.FromMinutes(2), m.GetParticipationTime(K("A"), now));
    }

    [Fact]
    public void GetParticipationTime_for_exited_player_uses_exit_time()
    {
        var startedAt = T0.AddSeconds(5);
        var m = JoinAll(BuildMatch(livesPerPlayer: 1, endPolicy: new KillCountEndPolicy(100)),
            at: startedAt);

        var deathTime = startedAt.AddSeconds(30);
        m.OnKill(K("A"), K("C"), deathTime);

        Assert.Equal(TimeSpan.FromSeconds(30), m.GetParticipationTime(K("C"), startedAt.AddMinutes(5)));
    }

    [Fact]
    public void GetParticipationTime_returns_null_before_match_starts()
    {
        var m = BuildMatch(livesPerPlayer: 3);
        Assert.Null(m.GetParticipationTime(K("A"), T0.AddSeconds(5)));
    }

    [Fact]
    public void Lives_below_one_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildMatch(livesPerPlayer: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildMatch(livesPerPlayer: -1));
    }

    [Fact]
    public void First_join_records_open_participation_period()
    {
        var m = BuildMatch();
        m.OnPlayerJoined(K("A"), T0.AddSeconds(5));

        var periods = m.GetParticipations(K("A"));
        Assert.Single(periods);
        Assert.Equal(T0.AddSeconds(5), periods[0].Enter);
        Assert.Null(periods[0].Exit);
        Assert.Equal(T0.AddSeconds(5), m.EnteredAt(K("A")));
    }

    [Fact]
    public void EnteredAt_is_null_until_first_join()
    {
        var m = BuildMatch();
        Assert.Null(m.EnteredAt(K("A")));
    }

    [Fact]
    public void Leaving_arena_closes_current_participation_period()
    {
        var m = JoinAll(BuildMatch());
        m.OnPlayerLeft(K("A"), T0.AddSeconds(30));

        var periods = m.GetParticipations(K("A"));
        Assert.Single(periods);
        Assert.NotNull(periods[0].Exit);
        Assert.Equal(T0.AddSeconds(30), periods[0].Exit!.Value);
    }

    [Fact]
    public void Returning_opens_a_new_period()
    {
        var m = JoinAll(BuildMatch());
        m.OnPlayerLeft(K("A"), T0.AddSeconds(30));
        m.OnPlayerReturned(K("A"), T0.AddSeconds(40));

        var periods = m.GetParticipations(K("A"));
        Assert.Equal(2, periods.Count);
        Assert.Equal(T0.AddSeconds(40), periods[1].Enter);
        Assert.Null(periods[1].Exit);
    }

    [Fact]
    public void Match_end_closes_all_open_periods()
    {
        var m = JoinAll(BuildMatch(endPolicy: new KillCountEndPolicy(1)));
        m.OnKill(K("A"), K("C"), T0.AddSeconds(60));   // ends the match

        Assert.Equal(MatchState.Completed, m.State);
        foreach (var p in new[] { K("A"), K("B"), K("C"), K("D") })
        {
            var periods = m.GetParticipations(p);
            Assert.NotEmpty(periods);
            Assert.NotNull(periods[^1].Exit);
        }
    }

    [Fact]
    public void Total_active_time_sums_all_periods_excluding_grace()
    {
        var m = JoinAll(BuildMatch());
        // A enters at T0+5 (per JoinAll default), leaves at +30, returns at +40, leaves at +60.
        m.OnPlayerLeft(K("A"), T0.AddSeconds(30));
        m.OnPlayerReturned(K("A"), T0.AddSeconds(40));
        m.OnPlayerLeft(K("A"), T0.AddSeconds(60));

        var total = m.GetTotalActiveTime(K("A"), T0.AddSeconds(120));
        // (30-5) + (60-40) = 25 + 20 = 45 seconds
        Assert.Equal(TimeSpan.FromSeconds(45), total);
    }

    [Fact]
    public void Total_active_time_includes_open_period_up_to_now()
    {
        var m = JoinAll(BuildMatch());
        var total = m.GetTotalActiveTime(K("A"), T0.AddSeconds(120));
        // Joined at +5 (from JoinAll), still active → 115 seconds.
        Assert.Equal(TimeSpan.FromSeconds(115), total);
    }

    [Fact]
    public void FinalDepartureAt_returns_last_period_exit()
    {
        var m = JoinAll(BuildMatch());
        m.OnPlayerLeft(K("A"), T0.AddSeconds(30));
        Assert.Equal(T0.AddSeconds(30), m.FinalDepartureAt(K("A")));

        m.OnPlayerReturned(K("A"), T0.AddSeconds(40));
        Assert.Null(m.FinalDepartureAt(K("A")));
    }

    [Fact]
    public void Cancelled_match_closes_open_periods()
    {
        var m = BuildMatch(joinTimeout: TimeSpan.FromMinutes(1));
        m.OnPlayerJoined(K("A"), T0.AddSeconds(5));
        // B/C/D never join.
        m.Tick(T0.AddMinutes(1));

        Assert.Equal(MatchState.Cancelled, m.State);
        var periods = m.GetParticipations(K("A"));
        Assert.Single(periods);
        Assert.NotNull(periods[0].Exit);
    }

    [Fact]
    public void Lives_exhaustion_alone_does_not_abandon_a_player()
    {
        // Player A burns through all their lives normally. They didn't "leave" — they died.
        // Should NOT be flagged as an abandoner.
        var m = JoinAll(BuildMatch(livesPerPlayer: 3, endPolicy: new KillCountEndPolicy(1000)));

        m.OnKill(K("C"), K("A"), T0.AddSeconds(10));
        m.OnKill(K("C"), K("A"), T0.AddSeconds(20));

        // A is out of lives. Match continues.
        Assert.Equal(0, m.LivesRemaining[K("A")]);
        Assert.Equal(MatchState.Live, m.State);
        Assert.NotNull(m.ExitedAt);
    }

    [Fact]
    public void Player_leaving_while_teammate_has_lives_is_abandonment_candidate()
    {
        var m = JoinAll(BuildMatch(livesPerPlayer: 3, graceWindow: TimeSpan.FromSeconds(30),
            endPolicy: new KillCountEndPolicy(1000)));

        m.OnPlayerLeft(K("A"), T0.AddSeconds(10));   // teammate B has lives
        m.Tick(T0.AddSeconds(45));                    // grace expires

        Assert.Equal(PlayerStatus.Abandoned, m.GetStatus(K("A")));
        // Match continues since B is still viable.
        Assert.Equal(MatchState.Live, m.State);
    }

    [Fact]
    public void Player_leaving_after_all_teammates_already_lost_lives_is_not_abandoner()
    {
        // Teammate B is already out of lives. A leaves. They didn't ditch anyone — B was already done.
        var m = JoinAll(BuildMatch(livesPerPlayer: 1, graceWindow: TimeSpan.FromSeconds(30),
            endPolicy: new KillCountEndPolicy(1000)));

        m.OnKill(K("C"), K("B"), T0.AddSeconds(10));   // B exhausted
        m.OnPlayerLeft(K("A"), T0.AddSeconds(20));     // A leaves with no viable teammate

        // The match will forfeit (only one team viable: C/D). At that point we close out.
        m.Tick(T0.AddSeconds(60));

        Assert.NotNull(m.Outcome);
        Assert.DoesNotContain(K("A"), m.Outcome!.AbandonedBy);
    }

    [Fact]
    public void Returning_within_grace_clears_candidate_abandoner_flag()
    {
        // A leaves, returns, leaves again. The first departure was redeemed; the second is fresh.
        var m = JoinAll(BuildMatch(livesPerPlayer: 1, graceWindow: TimeSpan.FromSeconds(30),
            endPolicy: new KillCountEndPolicy(1000)));

        m.OnPlayerLeft(K("A"), T0.AddSeconds(10));
        m.OnPlayerReturned(K("A"), T0.AddSeconds(20));
        m.OnPlayerLeft(K("A"), T0.AddSeconds(30));
        m.OnKill(K("C"), K("B"), T0.AddSeconds(40));   // B exhausted (A still in grace)
        m.Tick(T0.AddSeconds(65));                      // A's grace expires → forfeit triggers

        Assert.NotNull(m.Outcome);
        Assert.Contains(K("A"), m.Outcome!.AbandonedBy);
    }

    [Fact]
    public void Returning_then_match_ending_means_player_is_not_an_abandoner()
    {
        // A leaves, returns, the match completes. They should not be flagged.
        var m = JoinAll(BuildMatch(livesPerPlayer: 3, graceWindow: TimeSpan.FromSeconds(30),
            endPolicy: new KillCountEndPolicy(1)));

        m.OnPlayerLeft(K("A"), T0.AddSeconds(10));
        m.OnPlayerReturned(K("A"), T0.AddSeconds(20));
        m.OnKill(K("A"), K("C"), T0.AddSeconds(30));   // ends the match

        Assert.Equal(MatchState.Completed, m.State);
        Assert.DoesNotContain(K("A"), m.Outcome!.AbandonedBy);
    }

    [Fact]
    public void Last_team_standing_completes_match_as_forfeit_win()
    {
        // 4-team match: 3 teams forfeit by losing all lives, the last team wins.
        var teams = new IReadOnlyList<PlayerKey>[]
        {
            new[] { K("A") },
            new[] { K("B") },
            new[] { K("C") },
            new[] { K("D") },
        };
        var m = BuildMatch(teams: teams, livesPerPlayer: 1, endPolicy: new KillCountEndPolicy(1000));
        foreach (var team in m.Teams)
            foreach (var p in team)
                m.OnPlayerJoined(p, T0.AddSeconds(5));
        m.MarkLive(T0.AddSeconds(5));

        // A kills B, C, D — A is the last team standing.
        m.OnKill(K("A"), K("B"), T0.AddSeconds(10));
        m.OnKill(K("A"), K("C"), T0.AddSeconds(20));
        m.OnKill(K("A"), K("D"), T0.AddSeconds(30));

        Assert.Equal(MatchState.Completed, m.State);
        Assert.NotNull(m.Outcome);
        Assert.Contains(K("A"), m.Outcome!.RankedTeams[0].Players);
        Assert.Equal(1, m.Outcome.RankedTeams[0].Rank);
    }

    [Fact]
    public void No_show_during_forming_is_always_an_abandoner()
    {
        // Cancellation logic preserves the original semantic: failing to join is abandonment
        // regardless of the lives/teammate rule.
        var m = BuildMatch(joinTimeout: TimeSpan.FromMinutes(1));
        m.OnPlayerJoined(K("A"), T0.AddSeconds(5));
        m.OnPlayerJoined(K("B"), T0.AddSeconds(5));
        m.OnPlayerJoined(K("C"), T0.AddSeconds(5));
        // D never joins.
        m.Tick(T0.AddMinutes(1));

        Assert.Equal(MatchState.Cancelled, m.State);
        Assert.Contains(K("D"), m.Outcome!.AbandonedBy);
    }

    [Fact]
    public void OnKill_during_forming_is_ignored()
    {
        var m = BuildMatch();
        m.OnKill(K("A"), K("C"), T0.AddSeconds(1));
        Assert.Equal(0, m.KillsByTeam[0]);
    }

    [Fact]
    public void Reaching_target_kills_completes_the_match_with_winner_first()
    {
        var m = JoinAll(BuildMatch(endPolicy: new KillCountEndPolicy(3)));

        m.OnKill(K("A"), K("C"), T0.AddSeconds(10));
        m.OnKill(K("A"), K("D"), T0.AddSeconds(20));
        m.OnKill(K("B"), K("C"), T0.AddSeconds(30));

        Assert.Equal(MatchState.Completed, m.State);
        Assert.NotNull(m.Outcome);
        var ranked = m.Outcome!.RankedTeams;
        Assert.Equal(2, ranked.Count);
        Assert.Equal(1, ranked[0].Rank);
        Assert.Equal(3, ranked[0].Score);
        Assert.Contains(K("A"), ranked[0].Players);  // team 0 won
        Assert.Equal(2, ranked[1].Rank);
    }

    [Fact]
    public void Whole_team_leaving_with_other_team_viable_completes_as_forfeit()
    {
        var m = JoinAll(BuildMatch(graceWindow: TimeSpan.FromSeconds(30)));

        m.OnPlayerLeft(K("C"), T0.AddSeconds(10));
        m.OnPlayerLeft(K("D"), T0.AddSeconds(11));
        m.Tick(T0.AddSeconds(45));

        Assert.Equal(MatchState.Completed, m.State);
        Assert.NotNull(m.Outcome);
        Assert.Contains(K("C"), m.Outcome!.AbandonedBy);
        Assert.Contains(K("D"), m.Outcome.AbandonedBy);

        // Surviving team A/B wins by forfeit.
        Assert.Contains(K("A"), m.Outcome.RankedTeams[0].Players);
        Assert.Equal(1, m.Outcome.RankedTeams[0].Rank);
    }

    [Fact]
    public void Constructor_rejects_player_in_multiple_teams()
    {
        var bad = new IReadOnlyList<PlayerKey>[]
        {
            new[] { K("A"), K("B") },
            new[] { K("A"), K("C") },
        };
        Assert.Throws<ArgumentException>(() => BuildMatch(teams: bad));
    }

    [Fact]
    public void Constructor_rejects_default_player_key()
    {
        var bad = new IReadOnlyList<PlayerKey>[]
        {
            new[] { K("A"), default },
            new[] { K("C"), K("D") },
        };
        Assert.Throws<ArgumentException>(() => BuildMatch(teams: bad));
    }

    [Fact]
    public void Constructor_rejects_single_team()
    {
        var bad = new IReadOnlyList<PlayerKey>[] { new[] { K("A"), K("B") } };
        Assert.Throws<ArgumentException>(() => BuildMatch(teams: bad));
    }

    [Fact]
    public void TeamIndexOf_returns_assigned_team_or_null()
    {
        var m = BuildMatch();
        Assert.Equal(0, m.TeamIndexOf(K("A")));
        Assert.Equal(1, m.TeamIndexOf(K("D")));
        Assert.Null(m.TeamIndexOf(K("Ghost")));
    }

    [Fact]
    public void Leaving_during_Forming_moves_player_to_grace_not_abandoned()
    {
        // A placed player who specs before GO! (still Forming) gets the same per-player grace as
        // an in-progress departure -- not immediate abandonment. This is what lets them ?return
        // into the match instead of being stranded out of it (Abandoned blocks MarkLive).
        var m = BuildMatch();
        m.OnPlayerJoined(K("A"), T0.AddSeconds(1));
        m.OnPlayerLeft(K("A"), T0.AddSeconds(2));

        Assert.Equal(PlayerStatus.InGrace, m.GetStatus(K("A")));
    }

    [Fact]
    public void Returning_before_GO_forgives_a_Forming_departure()
    {
        // The headline behavior: a player specs during the countdown and returns before GO!. They
        // are forgiven (dropped from the candidate set, never marked Abandoned), the match starts
        // normally, and they are NOT recorded as an abandoner at match end.
        var m = BuildMatch(endPolicy: new KillCountEndPolicy(1));
        foreach (var p in new[] { K("A"), K("B"), K("C"), K("D") })
            m.OnPlayerJoined(p, T0.AddSeconds(1));

        m.OnPlayerLeft(K("A"), T0.AddSeconds(2));
        Assert.Equal(PlayerStatus.InGrace, m.GetStatus(K("A")));
        m.OnPlayerReturned(K("A"), T0.AddSeconds(3));
        Assert.Equal(PlayerStatus.Active, m.GetStatus(K("A")));
        Assert.False(m.IsCandidateAbandoner(K("A")));

        // GO! now succeeds (everyone Active again); the match plays out to completion.
        Assert.True(m.MarkLive(T0.AddSeconds(4)));
        m.OnKill(K("A"), K("C"), T0.AddSeconds(10));

        Assert.Equal(MatchState.Completed, m.State);
        Assert.DoesNotContain(K("A"), m.Outcome!.AbandonedBy);
    }

    [Fact]
    public void Forming_departure_that_never_returns_is_still_an_abandoner()
    {
        // Strand-protection is preserved: a placed player who bails before GO! and never comes
        // back has their grace expire (Tick flips them Abandoned), and the join-timeout
        // cancellation records them as an abandoner -- teammate B was still viable.
        var m = BuildMatch(joinTimeout: TimeSpan.FromMinutes(1), graceWindow: TimeSpan.FromSeconds(30));
        m.OnPlayerJoined(K("A"), T0.AddSeconds(1));
        m.OnPlayerJoined(K("B"), T0.AddSeconds(1));
        // C/D never join; A bails during Forming and never returns.
        m.OnPlayerLeft(K("A"), T0.AddSeconds(2));

        // Grace expires before the join-timeout -> A flips to Abandoned.
        m.Tick(T0.AddSeconds(40));
        Assert.Equal(PlayerStatus.Abandoned, m.GetStatus(K("A")));

        // Join-timeout cancels the match; A is recorded as an abandoner.
        m.Tick(T0.AddMinutes(1).AddSeconds(1));
        Assert.Equal(MatchState.Cancelled, m.State);
        Assert.Contains(K("A"), m.Outcome!.AbandonedBy);
    }

    [Fact]
    public void Cancel_during_forming_does_not_penalize_a_lone_leaver()
    {
        // The orchestrator's GO!-time cancel (a player was in spec at the start) uses the candidate
        // rule, identical to the join-timeout cancellation: a 1v1 leaver stranded no teammate, so
        // they are not flagged as an abandoner.
        var teams = new IReadOnlyList<PlayerKey>[] { new[] { K("A") }, new[] { K("B") } };
        var m = BuildMatch(teams: teams);
        m.OnPlayerJoined(K("A"), T0.AddSeconds(1));
        m.OnPlayerJoined(K("B"), T0.AddSeconds(1));
        m.OnPlayerLeft(K("A"), T0.AddSeconds(2));   // A specs during Forming -> InGrace

        m.Cancel(T0.AddSeconds(3));
        Assert.Equal(MatchState.Cancelled, m.State);
        Assert.Empty(m.Outcome!.AbandonedBy);
    }

    private static ActiveMatch JoinAll(ActiveMatch m, DateTimeOffset? at = null)
    {
        var when = at ?? T0.AddSeconds(5);
        foreach (var team in m.Teams)
            foreach (var p in team)
                m.OnPlayerJoined(p, when);
        m.MarkLive(when);
        return m;
    }
}
