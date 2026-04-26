using ClashEngine.Core.Eligibility;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Tests.Fakes;

namespace ClashEngine.Core.Tests.Eligibility;

public class PlayerEligibilityTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (PlayerEligibility e, PenaltyTracker p, FakeClock c) Build()
    {
        var clock = new FakeClock(T0);
        var tracker = new PenaltyTracker(
            PenaltyPolicy.DefaultAbandonment,
            PenaltyPolicy.DefaultGriefing);
        var elig = new PlayerEligibility(tracker, clock);
        return (elig, tracker, clock);
    }

    [Fact]
    public void Connected_no_match_no_timeout_is_Available()
    {
        var (e, _, _) = Build();
        var r = e.Check(K("Alice"), isConnected: true, isInActiveMatch: false);
        Assert.Equal(EligibilityStatus.Available, r.Status);
        Assert.Null(r.TimeoutUntil);
    }

    [Fact]
    public void Connected_in_match_is_InMatch()
    {
        var (e, _, _) = Build();
        var r = e.Check(K("Alice"), isConnected: true, isInActiveMatch: true);
        Assert.Equal(EligibilityStatus.InMatch, r.Status);
        Assert.Null(r.TimeoutUntil);
    }

    [Fact]
    public void Connected_in_abandonment_timeout_is_InTimeout_with_until()
    {
        var (e, p, c) = Build();
        p.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, c.UtcNow);

        var r = e.Check(K("Alice"), isConnected: true, isInActiveMatch: false);

        Assert.Equal(EligibilityStatus.InTimeout, r.Status);
        Assert.NotNull(r.TimeoutUntil);
        Assert.True(r.TimeoutUntil! > c.UtcNow);
    }

    [Fact]
    public void Connected_in_griefing_timeout_is_InTimeout()
    {
        var (e, p, c) = Build();
        p.RecordPenalty(K("Alice"), PenaltyKind.Griefing, c.UtcNow);

        Assert.Equal(EligibilityStatus.InTimeout, e.Check(K("Alice"), true, false).Status);
    }

    [Fact]
    public void InMatch_takes_priority_over_InTimeout()
    {
        var (e, p, c) = Build();
        p.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, c.UtcNow);

        var r = e.Check(K("Alice"), isConnected: true, isInActiveMatch: true);
        Assert.Equal(EligibilityStatus.InMatch, r.Status);
    }

    [Fact]
    public void Disconnected_overrides_all_other_states()
    {
        var (e, p, c) = Build();
        p.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, c.UtcNow);

        var r = e.Check(K("Alice"), isConnected: false, isInActiveMatch: true);
        Assert.Equal(EligibilityStatus.Disconnected, r.Status);
    }

    [Fact]
    public void Timeout_expires_with_clock_advance()
    {
        var (e, p, c) = Build();
        p.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, c.UtcNow);

        Assert.Equal(EligibilityStatus.InTimeout, e.Check(K("Alice"), true, false).Status);

        c.Advance(PenaltyPolicy.DefaultAbandonment.BaseTimeout + TimeSpan.FromSeconds(1));

        Assert.Equal(EligibilityStatus.Available, e.Check(K("Alice"), true, false).Status);
    }

    [Fact]
    public void Different_players_resolve_independently()
    {
        var (e, p, c) = Build();
        p.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, c.UtcNow);

        Assert.Equal(EligibilityStatus.InTimeout, e.Check(K("Alice"), true, false).Status);
        Assert.Equal(EligibilityStatus.Available, e.Check(K("Bob"), true, false).Status);
    }

    [Fact]
    public void Constructor_rejects_null_arguments()
    {
        var clock = new FakeClock();
        var tracker = new PenaltyTracker(PenaltyPolicy.DefaultAbandonment);

        Assert.Throws<ArgumentNullException>(() => new PlayerEligibility(null!, clock));
        Assert.Throws<ArgumentNullException>(() => new PlayerEligibility(tracker, null!));
    }
}
