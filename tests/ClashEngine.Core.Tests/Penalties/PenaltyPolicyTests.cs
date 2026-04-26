using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Tests.Penalties;

public class PenaltyPolicyTests
{
    [Fact]
    public void First_offense_returns_base_timeout()
    {
        var p = new PenaltyPolicy(PenaltyKind.Abandonment, TimeSpan.FromMinutes(10), 2.0, TimeSpan.FromHours(24));
        Assert.Equal(TimeSpan.FromMinutes(10), p.TimeoutForOffense(1));
    }

    [Fact]
    public void Second_offense_doubles_with_factor_two()
    {
        var p = new PenaltyPolicy(PenaltyKind.Abandonment, TimeSpan.FromMinutes(10), 2.0, TimeSpan.FromHours(24));
        Assert.Equal(TimeSpan.FromMinutes(20), p.TimeoutForOffense(2));
    }

    [Fact]
    public void Factor_one_means_no_escalation()
    {
        var p = new PenaltyPolicy(PenaltyKind.Griefing, TimeSpan.FromMinutes(5), 1.0, TimeSpan.FromHours(24));
        Assert.Equal(TimeSpan.FromMinutes(5), p.TimeoutForOffense(7));
    }

    [Fact]
    public void Validates_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PenaltyPolicy(PenaltyKind.Abandonment, TimeSpan.FromMinutes(-1), 2.0, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PenaltyPolicy(PenaltyKind.Abandonment, TimeSpan.FromMinutes(10), 0.5, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PenaltyPolicy(PenaltyKind.Abandonment, TimeSpan.FromMinutes(10), 2.0, TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public void TimeoutForOffense_rejects_zero_or_negative_count()
    {
        var p = PenaltyPolicy.DefaultAbandonment;
        Assert.Throws<ArgumentOutOfRangeException>(() => p.TimeoutForOffense(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.TimeoutForOffense(-1));
    }

    [Fact]
    public void Default_abandonment_has_sensible_values()
    {
        var p = PenaltyPolicy.DefaultAbandonment;
        Assert.Equal(PenaltyKind.Abandonment, p.Kind);
        Assert.Equal(TimeSpan.FromMinutes(10), p.BaseTimeout);
        Assert.Equal(2.0, p.EscalationFactor);
        Assert.Equal(TimeSpan.FromHours(24), p.MemoryWindow);
    }

    [Fact]
    public void Default_griefing_has_sensible_values()
    {
        var p = PenaltyPolicy.DefaultGriefing;
        Assert.Equal(PenaltyKind.Griefing, p.Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), p.BaseTimeout);
    }
}
