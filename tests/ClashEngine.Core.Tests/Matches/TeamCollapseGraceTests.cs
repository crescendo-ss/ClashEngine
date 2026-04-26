using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Tests.Matches;

public class TeamCollapseGraceTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ActiveMatch BuildAndStart(
        TimeSpan? graceWindow = null,
        TimeSpan? teamCollapseGrace = null,
        int? livesPerPlayer = null)
    {
        var m = new ActiveMatch(
            Guid.NewGuid(), new GameTypeId(1),
            new IReadOnlyList<PlayerKey>[]
            {
                new[] { K("A"), K("B") },
                new[] { K("C"), K("D") },
            },
            new KillCountEndPolicy(1000),
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: graceWindow ?? TimeSpan.FromMinutes(1),
            proposedAt: T0,
            livesPerPlayer: livesPerPlayer,
            teamCollapseGrace: teamCollapseGrace ?? TimeSpan.FromSeconds(10));

        foreach (var team in m.Teams)
            foreach (var p in team)
                m.OnPlayerJoined(p, T0.AddSeconds(1));
        return m;
    }

    [Fact]
    public void Team_with_all_members_left_forfeits_after_collapse_grace()
    {
        var m = BuildAndStart(
            graceWindow: TimeSpan.FromMinutes(1),
            teamCollapseGrace: TimeSpan.FromSeconds(10));

        m.OnPlayerLeft(K("C"), T0.AddSeconds(10));
        m.OnPlayerLeft(K("D"), T0.AddSeconds(11));

        // 5s after team collapsed: still in grace.
        m.Tick(T0.AddSeconds(15));
        Assert.Equal(MatchState.Live, m.State);

        // 11s after team collapsed (10s grace + 1s buffer): forfeit fires.
        m.Tick(T0.AddSeconds(22));
        Assert.Equal(MatchState.Completed, m.State);
        Assert.NotNull(m.Outcome);
        Assert.Contains(K("A"), m.Outcome!.RankedTeams[0].Players);
    }

    [Fact]
    public void Players_who_left_and_did_not_return_appear_in_AbandonedBy()
    {
        // Server can't tell quit from disconnect, so every leave is treated as candidate.
        // After team-collapse grace expires the players are flagged as abandoners.
        var m = BuildAndStart(teamCollapseGrace: TimeSpan.FromSeconds(10));

        m.OnPlayerLeft(K("C"), T0.AddSeconds(10));
        m.OnPlayerLeft(K("D"), T0.AddSeconds(11));
        m.Tick(T0.AddSeconds(25));

        Assert.Equal(MatchState.Completed, m.State);
        Assert.Contains(K("C"), m.Outcome!.AbandonedBy);
        Assert.Contains(K("D"), m.Outcome.AbandonedBy);
    }

    [Fact]
    public void Team_recovers_within_collapse_grace_no_forfeit()
    {
        var m = BuildAndStart(teamCollapseGrace: TimeSpan.FromSeconds(10));

        m.OnPlayerLeft(K("C"), T0.AddSeconds(10));
        m.OnPlayerLeft(K("D"), T0.AddSeconds(11));

        // C reconnects within team-collapse grace.
        m.OnPlayerReturned(K("C"), T0.AddSeconds(15));

        m.Tick(T0.AddSeconds(25));
        Assert.Equal(MatchState.Live, m.State);
    }

    [Fact]
    public void Returning_player_within_grace_is_not_in_AbandonedBy_when_match_ends()
    {
        // C leaves, returns within grace, match later completes via kill count.
        // Returning clears the candidate flag; C should not appear in AbandonedBy.
        var m = new ActiveMatch(
            Guid.NewGuid(), new GameTypeId(1),
            new IReadOnlyList<PlayerKey>[]
            {
                new[] { K("A"), K("B") },
                new[] { K("C"), K("D") },
            },
            new KillCountEndPolicy(1),
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromSeconds(30),
            proposedAt: T0,
            teamCollapseGrace: TimeSpan.FromSeconds(10));
        foreach (var team in m.Teams)
            foreach (var p in team)
                m.OnPlayerJoined(p, T0.AddSeconds(1));

        m.OnPlayerLeft(K("C"), T0.AddSeconds(10));
        m.OnPlayerReturned(K("C"), T0.AddSeconds(15));   // candidate cleared on return

        m.OnKill(K("A"), K("C"), T0.AddSeconds(20));     // A's team wins on first kill

        Assert.Equal(MatchState.Completed, m.State);
        Assert.DoesNotContain(K("C"), m.Outcome!.AbandonedBy);
    }

    [Fact]
    public void Lives_exhaustion_forfeits_immediately_no_grace()
    {
        // 4-team free-for-all with 1 life each. Player A kills B, C, D in quick succession.
        var teams = new IReadOnlyList<PlayerKey>[]
        {
            new[] { K("A") },
            new[] { K("B") },
            new[] { K("C") },
            new[] { K("D") },
        };
        var m = new ActiveMatch(
            Guid.NewGuid(), new GameTypeId(1), teams,
            new KillCountEndPolicy(1000),
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromSeconds(30),
            proposedAt: T0,
            livesPerPlayer: 1,
            teamCollapseGrace: TimeSpan.FromSeconds(10));
        foreach (var team in m.Teams)
            foreach (var p in team)
                m.OnPlayerJoined(p, T0.AddSeconds(1));

        m.OnKill(K("A"), K("B"), T0.AddSeconds(10));
        m.OnKill(K("A"), K("C"), T0.AddSeconds(11));
        m.OnKill(K("A"), K("D"), T0.AddSeconds(12));

        // Eliminated players can't recover. Forfeit is immediate.
        Assert.Equal(MatchState.Completed, m.State);
    }
}
