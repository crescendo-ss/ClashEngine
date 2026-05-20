using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Tests.Matches;

public class CompositeEndPolicyTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ActiveMatch StartedMatch(IMatchEndPolicy policy)
    {
        var m = new ActiveMatch(
            Guid.NewGuid(), "gt1",
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
    public void Constructor_rejects_empty_or_null_policy_lists()
    {
        Assert.Throws<ArgumentException>(() => new CompositeEndPolicy());
        Assert.Throws<ArgumentNullException>(() => new CompositeEndPolicy((IMatchEndPolicy[])null!));
        Assert.Throws<ArgumentException>(() => new CompositeEndPolicy(new IMatchEndPolicy[] { null! }));
    }

    [Fact]
    public void Returns_first_firing_policy_outcome()
    {
        var policy = new CompositeEndPolicy(
            new KillCountEndPolicy(2),
            new TimeLimitEndPolicy(TimeSpan.FromMinutes(20)));
        var m = StartedMatch(policy);

        // KillCount fires first: team 0 hits 2 kills well before the 20-minute limit.
        m.OnKill(K("A"), K("C"), T0.AddSeconds(30));
        m.OnKill(K("A"), K("D"), T0.AddSeconds(40));

        var outcome = policy.CheckOutcome(m, T0.AddSeconds(41));
        Assert.NotNull(outcome);
        Assert.Equal(2, outcome!.RankedTeams[0].Score);
    }

    [Fact]
    public void Time_limit_fires_when_kill_count_unreached()
    {
        var policy = new CompositeEndPolicy(
            new KillCountEndPolicy(targetKills: 100),     // never reached in this test
            new TimeLimitEndPolicy(TimeSpan.FromMinutes(20)));
        var m = StartedMatch(policy);

        m.OnKill(K("A"), K("C"), T0.AddSeconds(30));   // team 0 leads 1-0
        Assert.Null(policy.CheckOutcome(m, T0.AddMinutes(19)));   // before regulation

        var outcome = policy.CheckOutcome(m, T0.AddMinutes(20).AddSeconds(1));
        Assert.NotNull(outcome);
        Assert.Equal(1, outcome!.RankedTeams[0].Score);
    }

    [Fact]
    public void Earlier_listed_policy_wins_on_simultaneous_fire()
    {
        // Both fire at the same instant. The first-listed policy's outcome is returned.
        var killPolicy = new KillCountEndPolicy(targetKills: 1);
        var timePolicy = new TimeLimitEndPolicy(TimeSpan.FromSeconds(5));
        var policy = new CompositeEndPolicy(killPolicy, timePolicy);
        var m = StartedMatch(policy);

        // Match starts at T0+1s. Score one kill at T0+10s -- both policies will fire on the
        // very next CheckOutcome since regulation has elapsed AND a team has reached 1 kill.
        m.OnKill(K("A"), K("C"), T0.AddSeconds(10));
        var outcome = policy.CheckOutcome(m, T0.AddSeconds(11));
        Assert.NotNull(outcome);
        Assert.Equal(1, outcome!.RankedTeams[0].Score);

        // Sanity: both policies, when checked individually, fire on this state.
        Assert.NotNull(killPolicy.CheckOutcome(m, T0.AddSeconds(11)));
        Assert.NotNull(timePolicy.CheckOutcome(m, T0.AddSeconds(11)));
    }
}
