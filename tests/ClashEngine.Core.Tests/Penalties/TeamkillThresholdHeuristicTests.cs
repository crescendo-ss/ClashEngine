using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Tests.Penalties;

public class TeamkillThresholdHeuristicTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ActiveMatch MatchWithTeamkills(params (string Killer, string Victim)[] tks)
    {
        var m = new ActiveMatch(
            Guid.NewGuid(), new GameTypeId(1),
            new IReadOnlyList<PlayerKey>[]
            {
                new[] { K("A"), K("B") },
                new[] { K("C"), K("D") },
            },
            new KillCountEndPolicy(100),
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromSeconds(30),
            proposedAt: T0);

        foreach (var team in m.Teams)
            foreach (var p in team)
                m.OnPlayerJoined(p, T0.AddSeconds(1));
        m.MarkLive(T0.AddSeconds(1));

        for (int i = 0; i < tks.Length; i++)
            m.OnKill(K(tks[i].Killer), K(tks[i].Victim), T0.AddSeconds(10 + i));

        return m;
    }

    [Fact]
    public void No_teamkills_flags_nobody()
    {
        var m = MatchWithTeamkills();
        var flags = new TeamkillThresholdHeuristic(2).Evaluate(m);
        Assert.Empty(flags);
    }

    [Fact]
    public void Below_threshold_flags_nobody()
    {
        var m = MatchWithTeamkills(("A", "B"), ("A", "B"));  // 2 teamkills by A
        var flags = new TeamkillThresholdHeuristic(2).Evaluate(m);
        Assert.Empty(flags);
    }

    [Fact]
    public void Above_threshold_flags_player()
    {
        var m = MatchWithTeamkills(("A", "B"), ("A", "B"), ("A", "B"));  // 3 teamkills by A
        var flags = new TeamkillThresholdHeuristic(2).Evaluate(m);

        Assert.Single(flags);
        Assert.Equal(K("A"), flags[0].Player);
        Assert.Contains("3 teamkills", flags[0].Reason);
    }

    [Fact]
    public void Multiple_griefers_all_flagged()
    {
        var m = MatchWithTeamkills(
            ("A", "B"), ("A", "B"), ("A", "B"),  // A: 3 teamkills
            ("C", "D"), ("C", "D"), ("C", "D"), ("C", "D"));  // C: 4 teamkills
        var flags = new TeamkillThresholdHeuristic(2).Evaluate(m);

        Assert.Equal(2, flags.Count);
        Assert.Contains(flags, f => f.Player == K("A"));
        Assert.Contains(flags, f => f.Player == K("C"));
    }

    [Fact]
    public void Severity_at_minimum_flagged_count_is_one()
    {
        // threshold=2, minimum flagged = 3 teamkills, expected severity = 3 / (2+1) = 1.0.
        var m = MatchWithTeamkills(("A", "B"), ("A", "B"), ("A", "B"));
        var flag = new TeamkillThresholdHeuristic(2).Evaluate(m)[0];
        Assert.Equal(1.0, flag.Severity, precision: 6);
    }

    [Fact]
    public void Severity_grows_linearly_with_teamkills()
    {
        // threshold=2, 6 teamkills → severity = 6/3 = 2.0.
        var m = MatchWithTeamkills(
            ("A", "B"), ("A", "B"), ("A", "B"),
            ("A", "B"), ("A", "B"), ("A", "B"));
        var flag = new TeamkillThresholdHeuristic(2).Evaluate(m)[0];
        Assert.Equal(2.0, flag.Severity, precision: 6);
    }

    [Fact]
    public void Severity_caps_at_configured_value()
    {
        // 20 teamkills with threshold 0 → would be 20.0 / 1 = 20, but capped at 5.
        var ops = Enumerable.Range(0, 20).Select(_ => ("A", "B")).ToArray();
        var m = MatchWithTeamkills(ops);
        var flag = new TeamkillThresholdHeuristic(0, severityCap: 5.0).Evaluate(m)[0];
        Assert.Equal(5.0, flag.Severity, precision: 6);
    }

    [Fact]
    public void Severity_cap_below_one_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TeamkillThresholdHeuristic(2, severityCap: 0.5));
    }

    [Fact]
    public void NoGriefingHeuristic_always_empty()
    {
        var m = MatchWithTeamkills(("A", "B"), ("A", "B"), ("A", "B"));
        Assert.Empty(NoGriefingHeuristic.Instance.Evaluate(m));
    }

    [Fact]
    public void Threshold_validates_non_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TeamkillThresholdHeuristic(-1));
    }

    [Fact]
    public void Null_match_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TeamkillThresholdHeuristic(2).Evaluate(null!));
    }
}
