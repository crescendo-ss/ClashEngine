using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Tests.Matches;

public class ForfeitVoteTests
{
    private static PlayerKey K(string name) => new(name);

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ActiveMatch BuildMatch(
        IMatchEndPolicy? endPolicy = null,
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
            endPolicy ?? new KillCountEndPolicy(targetKills: 1000),
            TimeSpan.FromMinutes(1),
            graceWindow ?? TimeSpan.FromSeconds(30),
            T0,
            livesPerPlayer);
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

    [Fact]
    public void First_vote_is_Requested_with_progress_counts()
    {
        var m = JoinAll(BuildMatch());

        var vote = m.TryForfeit(K("A"), T0.AddSeconds(10));

        Assert.Equal(ForfeitVoteResult.Requested, vote.Result);
        Assert.Equal(1, vote.Votes);
        Assert.Equal(2, vote.Needed);
        Assert.True(m.HasForfeitVoted(K("A")));
        Assert.Equal(MatchState.Live, m.State);
    }

    [Fact]
    public void Unanimous_vote_ends_match_immediately_as_loss_with_no_abandons()
    {
        var m = JoinAll(BuildMatch());

        Assert.Equal(ForfeitVoteResult.Requested, m.TryForfeit(K("A"), T0.AddSeconds(10)).Result);
        var second = m.TryForfeit(K("B"), T0.AddSeconds(12));

        Assert.Equal(ForfeitVoteResult.Completed, second.Result);
        Assert.Equal(2, second.Votes);
        Assert.Equal(2, second.Needed);
        Assert.Equal(MatchState.Completed, m.State);
        Assert.NotNull(m.Outcome);
        // Opponents win by forfeit; the voting team takes the loss.
        Assert.Equal(1, m.Outcome!.RankedTeams[0].Rank);
        Assert.Contains(K("C"), m.Outcome.RankedTeams[0].Players);
        Assert.Contains(K("A"), m.Outcome.RankedTeams[1].Players);
        // The sanctioned exit: nobody is an abandoner.
        Assert.Empty(m.Outcome.AbandonedBy);
    }

    [Fact]
    public void Votes_are_sticky_and_duplicates_are_rejected()
    {
        var m = JoinAll(BuildMatch());

        m.TryForfeit(K("A"), T0.AddSeconds(10));
        var dup = m.TryForfeit(K("A"), T0.AddSeconds(20));

        Assert.Equal(ForfeitVoteResult.AlreadyVoted, dup.Result);
        Assert.Equal(1, dup.Votes);
        Assert.Equal(2, dup.Needed);
        Assert.Equal(MatchState.Live, m.State);
    }

    [Fact]
    public void Vote_before_GO_is_rejected()
    {
        var m = BuildMatch();   // still Forming
        Assert.Equal(ForfeitVoteResult.NotLive, m.TryForfeit(K("A"), T0.AddSeconds(1)).Result);
    }

    [Fact]
    public void Unknown_player_is_NotInMatch()
    {
        var m = JoinAll(BuildMatch());
        Assert.Equal(ForfeitVoteResult.NotInMatch, m.TryForfeit(K("Z"), T0.AddSeconds(10)).Result);
    }

    [Fact]
    public void Eliminated_player_cannot_vote_and_does_not_block_unanimity()
    {
        var m = JoinAll(BuildMatch(livesPerPlayer: 1));

        m.OnKill(K("C"), K("A"), T0.AddSeconds(10));   // A exhausted

        Assert.Equal(ForfeitVoteResult.Eliminated, m.TryForfeit(K("A"), T0.AddSeconds(11)).Result);

        // B is the only eligible voter left -- their lone vote is unanimous.
        var vote = m.TryForfeit(K("B"), T0.AddSeconds(12));
        Assert.Equal(ForfeitVoteResult.Completed, vote.Result);
        Assert.Equal(1, vote.Votes);
        Assert.Equal(1, vote.Needed);
        Assert.Equal(MatchState.Completed, m.State);
        Assert.Empty(m.Outcome!.AbandonedBy);
    }

    [Fact]
    public void Holdout_elimination_completes_a_pending_vote()
    {
        // A votes; B keeps fighting and loses their last life. "All non-eliminated players have
        // voted" just became true without B typing anything -- the team forfeits on the kill.
        var m = JoinAll(BuildMatch(livesPerPlayer: 1));

        m.TryForfeit(K("A"), T0.AddSeconds(10));
        m.OnKill(K("C"), K("B"), T0.AddSeconds(40));

        Assert.Equal(MatchState.Completed, m.State);
        Assert.Equal(1, m.Outcome!.RankedTeams[0].Rank);
        Assert.Contains(K("C"), m.Outcome.RankedTeams[0].Players);
        Assert.Empty(m.Outcome.AbandonedBy);
    }

    [Fact]
    public void Holdout_abandonment_completes_a_pending_vote_but_keeps_their_abandon()
    {
        // A votes and stays; B specs out without voting and lets their grace expire. B's
        // abandonment shrinks the denominator to just A, so the team forfeits -- but B never
        // blessed anything and keeps the abandon. A, who used the sanctioned path, is clean.
        var m = JoinAll(BuildMatch(graceWindow: TimeSpan.FromSeconds(30)));

        m.TryForfeit(K("A"), T0.AddSeconds(10));
        m.OnPlayerLeft(K("B"), T0.AddSeconds(15));
        m.Tick(T0.AddSeconds(50));                     // B's grace expires

        Assert.Equal(MatchState.Completed, m.State);
        Assert.Contains(K("B"), m.Outcome!.AbandonedBy);
        Assert.DoesNotContain(K("A"), m.Outcome.AbandonedBy);
    }

    [Fact]
    public void Voter_who_specced_out_is_forgiven_when_the_vote_completes()
    {
        // A votes, specs, and even lets their grace expire (normally a guaranteed abandon).
        // When B's vote later makes the team unanimous, A's earlier exit is retroactively the
        // first leg of a sanctioned forfeit -- no abandon.
        var m = JoinAll(BuildMatch(graceWindow: TimeSpan.FromSeconds(30)));

        m.TryForfeit(K("A"), T0.AddSeconds(10));
        m.OnPlayerLeft(K("A"), T0.AddSeconds(11));
        m.Tick(T0.AddSeconds(45));                     // A flips to Abandoned
        Assert.Equal(PlayerStatus.Abandoned, m.GetStatus(K("A")));
        Assert.Equal(MatchState.Live, m.State);        // B is still in -- match continues

        var vote = m.TryForfeit(K("B"), T0.AddSeconds(50));

        Assert.Equal(ForfeitVoteResult.Completed, vote.Result);
        Assert.Equal(MatchState.Completed, m.State);
        Assert.Empty(m.Outcome!.AbandonedBy);
    }

    [Fact]
    public void Partial_vote_keeps_the_match_running()
    {
        var m = JoinAll(BuildMatch());

        m.TryForfeit(K("A"), T0.AddSeconds(10));
        m.Tick(T0.AddSeconds(60));
        m.Tick(T0.AddSeconds(120));

        Assert.Equal(MatchState.Live, m.State);
        Assert.Null(m.Outcome);
    }

    [Fact]
    public void Solo_team_forfeits_on_a_single_vote()
    {
        var teams = new IReadOnlyList<PlayerKey>[] { new[] { K("A") }, new[] { K("C") } };
        var m = JoinAll(BuildMatch(teams: teams));

        var vote = m.TryForfeit(K("A"), T0.AddSeconds(10));

        Assert.Equal(ForfeitVoteResult.Completed, vote.Result);
        Assert.Equal(1, vote.Votes);
        Assert.Equal(1, vote.Needed);
        Assert.Equal(MatchState.Completed, m.State);
        Assert.Contains(K("C"), m.Outcome!.RankedTeams[0].Players);
    }

    [Fact]
    public void Votes_are_tallied_per_team()
    {
        // One vote on each team: neither team is unanimous, nothing ends.
        var m = JoinAll(BuildMatch());

        Assert.Equal(ForfeitVoteResult.Requested, m.TryForfeit(K("A"), T0.AddSeconds(10)).Result);
        Assert.Equal(ForfeitVoteResult.Requested, m.TryForfeit(K("C"), T0.AddSeconds(11)).Result);
        Assert.Equal(MatchState.Live, m.State);

        // Team 1 reaches unanimity first and takes the loss, pending team-0 votes notwithstanding.
        Assert.Equal(ForfeitVoteResult.Completed, m.TryForfeit(K("D"), T0.AddSeconds(12)).Result);
        Assert.Equal(MatchState.Completed, m.State);
        Assert.Contains(K("A"), m.Outcome!.RankedTeams[0].Players);
        Assert.Empty(m.Outcome.AbandonedBy);
    }

    [Fact]
    public void ForfeitEligibleTeammatesOf_excludes_eliminated_and_abandoned()
    {
        var teams = new IReadOnlyList<PlayerKey>[]
        {
            new[] { K("A"), K("B"), K("E") },
            new[] { K("C"), K("D"), K("F") },
        };
        var m = JoinAll(BuildMatch(teams: teams, livesPerPlayer: 1, graceWindow: TimeSpan.FromSeconds(30)));

        m.OnKill(K("C"), K("B"), T0.AddSeconds(10));   // B eliminated
        m.OnPlayerLeft(K("E"), T0.AddSeconds(11));
        m.Tick(T0.AddSeconds(45));                     // E abandoned

        Assert.Empty(m.ForfeitEligibleTeammatesOf(K("A")));

        // A is the sole remaining voter: their vote alone forfeits the team.
        Assert.Equal(ForfeitVoteResult.Completed, m.TryForfeit(K("A"), T0.AddSeconds(50)).Result);
    }
}
