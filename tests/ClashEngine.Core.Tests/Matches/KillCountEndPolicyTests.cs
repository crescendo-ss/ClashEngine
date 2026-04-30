using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Tests.Matches;

public class KillCountEndPolicyTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ActiveMatch StartedMatch(KillCountEndPolicy policy)
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
        m.MarkLive(T0.AddSeconds(1));
        return m;
    }

    [Fact]
    public void Below_target_returns_null()
    {
        var policy = new KillCountEndPolicy(5);
        var m = StartedMatch(policy);
        Assert.Null(policy.CheckOutcome(m, T0.AddSeconds(10)));
    }

    [Fact]
    public void Reaching_target_returns_outcome_with_winner_first()
    {
        var policy = new KillCountEndPolicy(2);
        var m = StartedMatch(policy);

        m.OnKill(K("C"), K("A"), T0.AddSeconds(10));
        m.OnKill(K("D"), K("B"), T0.AddSeconds(20));

        Assert.NotNull(m.Outcome);
        Assert.Equal(MatchState.Completed, m.State);
        Assert.Contains(K("C"), m.Outcome!.RankedTeams[0].Players);
        Assert.Equal(2, m.Outcome.RankedTeams[0].Score);
        Assert.Equal(0, m.Outcome.RankedTeams[1].Score);
    }

    [Fact]
    public void Tie_breaks_by_team_index_ascending()
    {
        var policy = new KillCountEndPolicy(2);
        var m = StartedMatch(policy);
        m.OnKill(K("A"), K("C"), T0.AddSeconds(1));
        m.OnKill(K("A"), K("D"), T0.AddSeconds(2));
        // Team 0 reached target first.

        Assert.Equal(2, m.Outcome!.RankedTeams[0].Score);
        Assert.Contains(K("A"), m.Outcome.RankedTeams[0].Players);
    }

    [Fact]
    public void Validates_target_is_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KillCountEndPolicy(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KillCountEndPolicy(-1));
    }
}
