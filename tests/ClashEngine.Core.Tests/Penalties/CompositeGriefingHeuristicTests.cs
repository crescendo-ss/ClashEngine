using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Tests.Penalties;

public class CompositeGriefingHeuristicTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class StubHeuristic(params GriefingFlag[] flags) : IGriefingHeuristic
    {
        public IReadOnlyList<GriefingFlag> Evaluate(ActiveMatch match) => flags;
    }

    private static ActiveMatch AnyMatch() => new(
        Guid.NewGuid(), "gt1",
        new IReadOnlyList<PlayerKey>[]
        {
            new[] { K("A"), K("B") },
            new[] { K("C"), K("D") },
        },
        new KillCountEndPolicy(100),
        joinTimeout: TimeSpan.FromMinutes(1),
        graceWindow: TimeSpan.FromSeconds(30),
        proposedAt: T0);

    [Fact]
    public void Concatenates_flags_from_all_children()
    {
        var composite = new CompositeGriefingHeuristic(
            new StubHeuristic(new GriefingFlag(K("A"), "left early", 2.0)),
            new StubHeuristic(new GriefingFlag(K("C"), "teamkills", 1.5)));

        var flags = composite.Evaluate(AnyMatch());

        Assert.Equal(2, flags.Count);
        Assert.Contains(flags, f => f.Player == K("A") && f.Reason == "left early");
        Assert.Contains(flags, f => f.Player == K("C") && f.Reason == "teamkills");
    }

    [Fact]
    public void Player_flagged_by_multiple_children_keeps_only_the_highest_severity_flag()
    {
        var composite = new CompositeGriefingHeuristic(
            new StubHeuristic(new GriefingFlag(K("A"), "left early", 2.0)),
            new StubHeuristic(new GriefingFlag(K("A"), "teamkills", 3.5)));

        var flags = composite.Evaluate(AnyMatch());

        var flag = Assert.Single(flags);
        Assert.Equal(K("A"), flag.Player);
        Assert.Equal("teamkills", flag.Reason);
        Assert.Equal(3.5, flag.Severity);
    }

    [Fact]
    public void Earlier_child_wins_severity_ties()
    {
        var composite = new CompositeGriefingHeuristic(
            new StubHeuristic(new GriefingFlag(K("A"), "first", 2.0)),
            new StubHeuristic(new GriefingFlag(K("A"), "second", 2.0)));

        var flag = Assert.Single(composite.Evaluate(AnyMatch()));
        Assert.Equal("first", flag.Reason);
    }

    [Fact]
    public void No_flags_from_any_child_yields_empty()
    {
        var composite = new CompositeGriefingHeuristic(new StubHeuristic(), new StubHeuristic());
        Assert.Empty(composite.Evaluate(AnyMatch()));
    }

    [Fact]
    public void Constructor_requires_at_least_one_child()
    {
        Assert.Throws<ArgumentException>(() => new CompositeGriefingHeuristic());
    }

    [Fact]
    public void Constructor_rejects_null_children()
    {
        Assert.Throws<ArgumentNullException>(() => new CompositeGriefingHeuristic((IGriefingHeuristic[])null!));
        Assert.Throws<ArgumentNullException>(() => new CompositeGriefingHeuristic(new StubHeuristic(), null!));
    }

    [Fact]
    public void Null_match_throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CompositeGriefingHeuristic(new StubHeuristic()).Evaluate(null!));
    }
}
