using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Tests.Matches;

public class TimeLimitEndPolicyTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ActiveMatch StartedMatch(IMatchEndPolicy policy)
    {
        var m = new ActiveMatch(
            Guid.NewGuid(), new GameTypeId(1),
            new IReadOnlyList<PlayerKey>[]
            {
                new[] { K("A"), K("B") },
                new[] { K("C"), K("D") },
            },
            policy,
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromSeconds(30),
            proposedAt: T0);

        foreach (var team in m.Teams)
            foreach (var p in team)
                m.OnPlayerJoined(p, T0.AddSeconds(1));
        m.MarkLive(T0.AddSeconds(1));   // match becomes Live at T0+1s
        return m;
    }

    [Fact]
    public void Constructor_rejects_zero_or_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeLimitEndPolicy(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeLimitEndPolicy(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Before_regulation_ends_returns_null()
    {
        var policy = new TimeLimitEndPolicy(TimeSpan.FromMinutes(20));
        var m = StartedMatch(policy);
        m.OnKill(K("A"), K("C"), T0.AddSeconds(30));   // some scoring within regulation
        Assert.Null(policy.CheckOutcome(m, T0.AddMinutes(19)));
    }

    [Fact]
    public void At_regulation_with_unique_leader_ends_match()
    {
        var policy = new TimeLimitEndPolicy(TimeSpan.FromMinutes(20));
        var m = StartedMatch(policy);
        m.OnKill(K("A"), K("C"), T0.AddSeconds(30));   // team 0 leads 1-0
        m.OnKill(K("A"), K("D"), T0.AddSeconds(40));   // team 0 leads 2-0

        var outcome = policy.CheckOutcome(m, T0.AddMinutes(20).AddSeconds(1));
        Assert.NotNull(outcome);
        Assert.Equal(MatchState.Completed, outcome!.FinalState);
        Assert.Equal(2, outcome.RankedTeams[0].Score);
        Assert.Equal(0, outcome.RankedTeams[1].Score);
        // Team 0's players (A, B) should be in the rank-1 entry.
        Assert.Contains(K("A"), outcome.RankedTeams[0].Players);
        Assert.Contains(K("B"), outcome.RankedTeams[0].Players);
    }

    [Fact]
    public void At_regulation_with_tied_score_continues_into_sudden_death()
    {
        var policy = new TimeLimitEndPolicy(TimeSpan.FromMinutes(20));
        var m = StartedMatch(policy);
        m.OnKill(K("A"), K("C"), T0.AddSeconds(30));   // 1-0
        m.OnKill(K("C"), K("A"), T0.AddSeconds(60));   // 1-1 (tied)

        // 20 minutes hits, still tied -- no outcome yet (sudden death).
        Assert.Null(policy.CheckOutcome(m, T0.AddMinutes(20).AddSeconds(1)));
        Assert.Null(policy.CheckOutcome(m, T0.AddMinutes(25)));
    }

    [Fact]
    public void Sudden_death_kill_breaks_tie_and_ends_match()
    {
        var policy = new TimeLimitEndPolicy(TimeSpan.FromMinutes(20));
        var m = StartedMatch(policy);
        m.OnKill(K("A"), K("C"), T0.AddSeconds(30));   // 1-0
        m.OnKill(K("C"), K("A"), T0.AddSeconds(60));   // 1-1 (tied at regulation)

        Assert.Null(policy.CheckOutcome(m, T0.AddMinutes(20).AddSeconds(1)));

        // 5 minutes into sudden death, team 1 scores.
        m.OnKill(K("D"), K("B"), T0.AddMinutes(25));   // 1-2
        var outcome = policy.CheckOutcome(m, T0.AddMinutes(25).AddSeconds(1));
        Assert.NotNull(outcome);
        Assert.Contains(K("C"), outcome!.RankedTeams[0].Players);
        Assert.Contains(K("D"), outcome.RankedTeams[0].Players);
        Assert.Equal(2, outcome.RankedTeams[0].Score);
        Assert.Equal(1, outcome.RankedTeams[1].Score);
    }

    [Fact]
    public void Sudden_death_unbounded_when_teams_keep_trading()
    {
        var policy = new TimeLimitEndPolicy(TimeSpan.FromMinutes(20));
        var m = StartedMatch(policy);
        m.OnKill(K("A"), K("C"), T0.AddSeconds(30));   // 1-0
        m.OnKill(K("C"), K("A"), T0.AddSeconds(60));   // 1-1

        // Trade kills well past regulation; never ends until tie breaks.
        m.OnKill(K("A"), K("D"), T0.AddMinutes(21));   // 2-1 -> would end here
        var outcome = policy.CheckOutcome(m, T0.AddMinutes(21).AddSeconds(1));
        Assert.NotNull(outcome);
    }

    [Fact]
    public void Match_not_started_yet_returns_null()
    {
        // Build a match but don't transition any player to Active -- StartedAt is null.
        var policy = new TimeLimitEndPolicy(TimeSpan.FromMinutes(20));
        var m = new ActiveMatch(
            Guid.NewGuid(), new GameTypeId(1),
            new IReadOnlyList<PlayerKey>[]
            {
                new[] { K("A") },
                new[] { K("C") },
            },
            policy,
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromSeconds(30),
            proposedAt: T0);

        Assert.Null(policy.CheckOutcome(m, T0.AddMinutes(30)));   // never started
    }
}
