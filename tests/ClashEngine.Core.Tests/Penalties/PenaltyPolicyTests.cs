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

    [Fact]
    public void EffectiveTimeoutFor_caps_at_max_timeout()
    {
        var p = new PenaltyPolicy(
            PenaltyKind.StagingAfk,
            baseTimeout: TimeSpan.FromMinutes(1),
            escalationFactor: 2.0,
            memoryWindow: TimeSpan.FromHours(24),
            maxTimeout: TimeSpan.FromHours(6));

        // Raw ladder at offense 20 = 1m * 2^19 ~= 8738h, way past 6h.
        Assert.Equal(TimeSpan.FromHours(6), p.EffectiveTimeoutFor(20));

        // Far below the cap: returns the raw value.
        Assert.Equal(TimeSpan.FromMinutes(2), p.EffectiveTimeoutFor(2));
    }

    [Fact]
    public void EffectiveTimeoutFor_caps_after_severity_multiplier()
    {
        var p = new PenaltyPolicy(
            PenaltyKind.Griefing,
            baseTimeout: TimeSpan.FromHours(2),
            escalationFactor: 1.0,
            memoryWindow: TimeSpan.FromHours(24),
            maxTimeout: TimeSpan.FromHours(6));

        // 2h * severity 5 = 10h, capped to 6h.
        Assert.Equal(TimeSpan.FromHours(6), p.EffectiveTimeoutFor(1, severity: 5.0));
        // 2h * severity 2 = 4h, below cap.
        Assert.Equal(TimeSpan.FromHours(4), p.EffectiveTimeoutFor(1, severity: 2.0));
    }

    [Fact]
    public void Default_max_timeout_is_six_hours()
    {
        Assert.Equal(TimeSpan.FromHours(6), PenaltyPolicy.DefaultMaxTimeout);
        Assert.Equal(TimeSpan.FromHours(6), PenaltyPolicy.DefaultAbandonment.MaxTimeout);
        Assert.Equal(TimeSpan.FromHours(6), PenaltyPolicy.DefaultGriefing.MaxTimeout);
        Assert.Equal(TimeSpan.FromHours(6), PenaltyPolicy.DefaultStagingAfk.MaxTimeout);
    }

    [Fact]
    public void WithMaxTimeout_returns_a_copy_with_the_new_cap()
    {
        var original = PenaltyPolicy.DefaultStagingAfk;
        var customised = original.WithMaxTimeout(TimeSpan.FromHours(2));

        Assert.Equal(TimeSpan.FromHours(2), customised.MaxTimeout);
        Assert.Equal(original.Kind, customised.Kind);
        Assert.Equal(original.BaseTimeout, customised.BaseTimeout);
        Assert.Equal(original.EscalationFactor, customised.EscalationFactor);
        Assert.Equal(original.MemoryWindow, customised.MemoryWindow);
        // Original is untouched.
        Assert.Equal(TimeSpan.FromHours(6), original.MaxTimeout);
    }

    [Fact]
    public void Constructor_rejects_non_positive_max_timeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PenaltyPolicy(PenaltyKind.Abandonment, TimeSpan.FromMinutes(10), 2.0, TimeSpan.FromHours(1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PenaltyPolicy(PenaltyKind.Abandonment, TimeSpan.FromMinutes(10), 2.0, TimeSpan.FromHours(1), TimeSpan.FromMinutes(-1)));
    }
}
