using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Tests.Penalties;

public class EarlyExitHeuristicTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ActiveMatch BuildAndStart(int lives, DateTimeOffset startedAt)
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
            proposedAt: T0,
            livesPerPlayer: lives);

        foreach (var team in m.Teams)
            foreach (var p in team)
                m.OnPlayerJoined(p, startedAt);
        return m;
    }

    [Fact]
    public void No_lives_configured_means_no_flags()
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
            proposedAt: T0);  // no lives

        Assert.Empty(new EarlyExitHeuristic(minimumDuration: TimeSpan.FromSeconds(60)).Evaluate(m));
    }

    [Fact]
    public void Player_with_lives_remaining_at_match_end_is_not_flagged()
    {
        var m = BuildAndStart(lives: 3, startedAt: T0.AddSeconds(1));
        // Nobody dies; the match runs and somehow ends.
        var heuristic = new EarlyExitHeuristic(minimumDuration: TimeSpan.FromMinutes(2));
        Assert.Empty(heuristic.Evaluate(m));
    }

    [Fact]
    public void Player_who_burned_lives_quickly_is_flagged_by_minimum_duration()
    {
        var startedAt = T0.AddSeconds(1);
        var m = BuildAndStart(lives: 2, startedAt: startedAt);

        m.OnKill(K("A"), K("C"), startedAt.AddSeconds(10));
        m.OnKill(K("A"), K("C"), startedAt.AddSeconds(15));  // C exits at +15s

        var heuristic = new EarlyExitHeuristic(minimumDuration: TimeSpan.FromSeconds(60));
        var flags = heuristic.Evaluate(m);

        Assert.Single(flags);
        Assert.Equal(K("C"), flags[0].Player);
        Assert.Contains("2 lives", flags[0].Reason);
    }

    [Fact]
    public void Player_who_lasted_long_enough_is_not_flagged()
    {
        var startedAt = T0.AddSeconds(1);
        var m = BuildAndStart(lives: 2, startedAt: startedAt);

        m.OnKill(K("A"), K("C"), startedAt.AddMinutes(10));
        m.OnKill(K("A"), K("C"), startedAt.AddMinutes(20));  // C exits after 20 min

        var heuristic = new EarlyExitHeuristic(minimumDuration: TimeSpan.FromMinutes(5));
        Assert.Empty(heuristic.Evaluate(m));
    }

    [Fact]
    public void Multiple_quick_exits_all_flagged()
    {
        var startedAt = T0.AddSeconds(1);
        var m = BuildAndStart(lives: 1, startedAt: startedAt);

        m.OnKill(K("A"), K("C"), startedAt.AddSeconds(10));   // C exits
        m.OnKill(K("A"), K("D"), startedAt.AddSeconds(20));   // D exits

        var heuristic = new EarlyExitHeuristic(minimumDuration: TimeSpan.FromMinutes(1));
        var flags = heuristic.Evaluate(m);

        Assert.Equal(2, flags.Count);
        Assert.Contains(flags, f => f.Player == K("C"));
        Assert.Contains(flags, f => f.Player == K("D"));
    }

    [Fact]
    public void Fractional_threshold_uses_match_duration()
    {
        // Match lasts 100s. C exits at 10s (10% fraction). 25% threshold flags.
        var startedAt = T0.AddSeconds(1);
        var m = BuildAndStart(lives: 1, startedAt: startedAt);
        m.OnKill(K("A"), K("C"), startedAt.AddSeconds(10));

        // To set EndedAt, drive the match to completion via the end policy.
        // KillCountEndPolicy(100) is too high; just kill 100 times for D... actually let's use a low end policy.
        // We can build a separate match here for the fractional check.
        var m2 = new ActiveMatch(
            Guid.NewGuid(), new GameTypeId(1),
            new IReadOnlyList<PlayerKey>[]
            {
                new[] { K("A"), K("B") },
                new[] { K("C"), K("D") },
            },
            new KillCountEndPolicy(2),
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromSeconds(30),
            proposedAt: T0,
            livesPerPlayer: 1);

        foreach (var team in m2.Teams)
            foreach (var p in team)
                m2.OnPlayerJoined(p, startedAt);

        m2.OnKill(K("A"), K("C"), startedAt.AddSeconds(10));   // C exits at +10s
        m2.OnKill(K("A"), K("D"), startedAt.AddSeconds(100));  // D exits at +100s — match ends here

        Assert.Equal(MatchState.Completed, m2.State);

        var heuristic = new EarlyExitHeuristic(minimumFractionOfMatch: 0.25);
        var flags = heuristic.Evaluate(m2);

        Assert.Single(flags);
        Assert.Equal(K("C"), flags[0].Player);
    }

    [Fact]
    public void Constructor_requires_at_least_one_threshold()
    {
        Assert.Throws<ArgumentException>(() => new EarlyExitHeuristic());
    }

    [Fact]
    public void Constructor_validates_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EarlyExitHeuristic(minimumDuration: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EarlyExitHeuristic(minimumFractionOfMatch: -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EarlyExitHeuristic(minimumFractionOfMatch: 1.1));
    }

    [Fact]
    public void Null_match_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EarlyExitHeuristic(minimumDuration: TimeSpan.FromSeconds(60)).Evaluate(null!));
    }

    [Fact]
    public void Severity_grows_as_exit_gets_earlier()
    {
        var startedAt = T0.AddSeconds(1);

        var mLate = BuildAndStart(lives: 1, startedAt: startedAt);
        mLate.OnKill(K("A"), K("C"), startedAt.AddSeconds(50));

        var mEarly = BuildAndStart(lives: 1, startedAt: startedAt);
        mEarly.OnKill(K("A"), K("C"), startedAt.AddSeconds(5));

        var heuristic = new EarlyExitHeuristic(minimumDuration: TimeSpan.FromSeconds(60));
        var lateFlag = heuristic.Evaluate(mLate)[0];
        var earlyFlag = heuristic.Evaluate(mEarly)[0];

        Assert.True(earlyFlag.Severity > lateFlag.Severity,
            $"early ({earlyFlag.Severity}) should exceed late ({lateFlag.Severity})");
    }

    [Fact]
    public void Severity_at_threshold_is_around_one()
    {
        var startedAt = T0.AddSeconds(1);
        var m = BuildAndStart(lives: 1, startedAt: startedAt);
        m.OnKill(K("A"), K("C"), startedAt.AddSeconds(59));   // 1s under 60s minimum

        var flag = new EarlyExitHeuristic(minimumDuration: TimeSpan.FromSeconds(60)).Evaluate(m)[0];
        // 60 / 59 ≈ 1.017
        Assert.InRange(flag.Severity, 1.0, 1.1);
    }

    [Fact]
    public void Severity_caps_at_configured_value()
    {
        var startedAt = T0.AddSeconds(1);
        var m = BuildAndStart(lives: 1, startedAt: startedAt);
        m.OnKill(K("A"), K("C"), startedAt.AddSeconds(1));   // 1s of 600s minimum → uncapped severity 600

        var flag = new EarlyExitHeuristic(
            minimumDuration: TimeSpan.FromSeconds(600),
            severityCap: 5.0).Evaluate(m)[0];
        Assert.Equal(5.0, flag.Severity, precision: 6);
    }

    [Fact]
    public void Severity_cap_below_one_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EarlyExitHeuristic(minimumDuration: TimeSpan.FromSeconds(60), severityCap: 0.5));
    }
}
