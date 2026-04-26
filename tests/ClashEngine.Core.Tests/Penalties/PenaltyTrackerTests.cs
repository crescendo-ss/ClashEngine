using ClashEngine.Core.Identity;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Tests.Penalties;

public class PenaltyTrackerTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PenaltyTracker Tracker(
        TimeSpan? abandonBase = null, double abandonFactor = 2.0, TimeSpan? abandonMemory = null,
        TimeSpan? griefBase = null, double griefFactor = 2.0, TimeSpan? griefMemory = null)
    {
        return new PenaltyTracker(
            new PenaltyPolicy(PenaltyKind.Abandonment,
                abandonBase ?? TimeSpan.FromMinutes(10),
                abandonFactor,
                abandonMemory ?? TimeSpan.FromHours(24)),
            new PenaltyPolicy(PenaltyKind.Griefing,
                griefBase ?? TimeSpan.FromMinutes(5),
                griefFactor,
                griefMemory ?? TimeSpan.FromHours(24)));
    }

    [Fact]
    public void New_tracker_has_no_records()
    {
        var t = Tracker();
        Assert.Equal(0, t.GetOffenseCount(K("Alice"), PenaltyKind.Abandonment));
        Assert.Null(t.TimeoutUntil(K("Alice")));
        Assert.False(t.IsInTimeout(K("Alice"), T0));
    }

    [Fact]
    public void First_record_returns_offense_one_and_sets_timeout()
    {
        var t = Tracker();
        Assert.Equal(1, t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0));

        Assert.Equal(T0 + TimeSpan.FromMinutes(10), t.TimeoutUntil(K("Alice")));
        Assert.True(t.IsInTimeout(K("Alice"), T0));
        Assert.False(t.IsInTimeout(K("Alice"), T0 + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Repeat_within_memory_window_escalates()
    {
        var t = Tracker();
        t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0);
        Assert.Equal(2, t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0 + TimeSpan.FromMinutes(30)));

        Assert.Equal(T0 + TimeSpan.FromMinutes(30) + TimeSpan.FromMinutes(20), t.TimeoutUntil(K("Alice")));
    }

    [Fact]
    public void Repeat_outside_memory_window_resets_count()
    {
        var t = Tracker(abandonMemory: TimeSpan.FromHours(24));
        t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0);
        Assert.Equal(1, t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0 + TimeSpan.FromHours(25)));
    }

    [Fact]
    public void Different_kinds_have_separate_ladders()
    {
        var t = Tracker();
        t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0);
        t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0 + TimeSpan.FromMinutes(1));

        Assert.Equal(1, t.GetOffenseCount(K("Alice"), PenaltyKind.Abandonment));
        Assert.Equal(1, t.GetOffenseCount(K("Alice"), PenaltyKind.Griefing));
    }

    [Fact]
    public void TimeoutUntil_returns_max_across_kinds()
    {
        var t = Tracker(
            abandonBase: TimeSpan.FromMinutes(10),
            griefBase: TimeSpan.FromMinutes(20));

        t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0);
        t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0);

        Assert.Equal(T0 + TimeSpan.FromMinutes(20), t.TimeoutUntil(K("Alice")));
    }

    [Fact]
    public void RescindMostRecent_removes_latest_event_and_decrements_count()
    {
        var t = Tracker();
        t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0);
        t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0 + TimeSpan.FromMinutes(1));

        Assert.True(t.RescindMostRecent(K("Alice"), PenaltyKind.Griefing));
        Assert.Equal(1, t.GetOffenseCount(K("Alice"), PenaltyKind.Griefing));
    }

    [Fact]
    public void Rescinding_only_event_clears_timeout_entirely()
    {
        var t = Tracker();
        t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0);
        t.RescindMostRecent(K("Alice"), PenaltyKind.Griefing);

        Assert.Equal(0, t.GetOffenseCount(K("Alice"), PenaltyKind.Griefing));
        Assert.Null(t.TimeoutUntil(K("Alice")));
    }

    [Fact]
    public void Rescinding_when_no_events_returns_false()
    {
        var t = Tracker();
        Assert.False(t.RescindMostRecent(K("Alice"), PenaltyKind.Griefing));
    }

    [Fact]
    public void Rescinding_one_kind_does_not_affect_other_kinds()
    {
        var t = Tracker();
        t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0);
        t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0);

        t.RescindMostRecent(K("Alice"), PenaltyKind.Griefing);

        Assert.Equal(1, t.GetOffenseCount(K("Alice"), PenaltyKind.Abandonment));
        Assert.Equal(0, t.GetOffenseCount(K("Alice"), PenaltyKind.Griefing));
    }

    [Fact]
    public void Snapshot_round_trips_via_Rehydrate()
    {
        var t = Tracker();
        t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0);
        t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0 + TimeSpan.FromMinutes(5));
        t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0 + TimeSpan.FromMinutes(2));
        t.RecordPenalty(K("Bob"), PenaltyKind.Abandonment, T0);

        var snap = t.Snapshot();
        Assert.Equal(4, snap.Count);

        var t2 = Tracker();
        t2.Rehydrate(snap);
        Assert.Equal(2, t2.GetOffenseCount(K("Alice"), PenaltyKind.Abandonment));
        Assert.Equal(1, t2.GetOffenseCount(K("Alice"), PenaltyKind.Griefing));
        Assert.Equal(1, t2.GetOffenseCount(K("Bob"), PenaltyKind.Abandonment));
    }

    [Fact]
    public void Player_lookup_is_case_insensitive()
    {
        var t = Tracker();
        t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0);
        Assert.True(t.IsInTimeout(K("ALICE"), T0));
        Assert.Equal(1, t.GetOffenseCount(K("alice"), PenaltyKind.Abandonment));
    }

    [Fact]
    public void Prune_removes_records_past_both_timeout_and_memory()
    {
        var t = Tracker(abandonBase: TimeSpan.FromMinutes(10), abandonMemory: TimeSpan.FromHours(1));
        t.RecordPenalty(K("Alice"), PenaltyKind.Abandonment, T0);

        Assert.Equal(0, t.Prune(T0 + TimeSpan.FromMinutes(30)));
        Assert.Equal(1, t.Prune(T0 + TimeSpan.FromMinutes(90)));
        Assert.Null(t.TimeoutUntil(K("Alice")));
    }

    [Fact]
    public void Recording_for_unknown_kind_throws()
    {
        var t = new PenaltyTracker(PenaltyPolicy.DefaultAbandonment);  // no Griefing policy
        Assert.Throws<InvalidOperationException>(() =>
            t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0));
    }

    [Fact]
    public void Constructor_rejects_duplicate_kinds()
    {
        Assert.Throws<ArgumentException>(() =>
            new PenaltyTracker(
                PenaltyPolicy.DefaultAbandonment,
                new PenaltyPolicy(PenaltyKind.Abandonment, TimeSpan.FromSeconds(1), 1.0, TimeSpan.FromSeconds(1))));
    }

    [Fact]
    public void RecordPenalty_with_default_player_throws()
    {
        var t = Tracker();
        Assert.Throws<ArgumentException>(() => t.RecordPenalty(default, PenaltyKind.Abandonment, T0));
    }

    [Fact]
    public void Severity_below_one_throws()
    {
        var t = Tracker();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0, severity: 0.5));
    }

    [Fact]
    public void Severity_scales_timeout_linearly()
    {
        var t = Tracker(griefBase: TimeSpan.FromMinutes(5));
        t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0, severity: 3.0);

        // Base 5min × severity 3 = 15min.
        Assert.Equal(T0 + TimeSpan.FromMinutes(15), t.TimeoutEndForKind(K("Alice"), PenaltyKind.Griefing));
    }

    [Fact]
    public void Severity_one_is_equivalent_to_default()
    {
        var t1 = Tracker(griefBase: TimeSpan.FromMinutes(5));
        var t2 = Tracker(griefBase: TimeSpan.FromMinutes(5));

        t1.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0);
        t2.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0, severity: 1.0);

        Assert.Equal(t1.TimeoutEndForKind(K("Alice"), PenaltyKind.Griefing),
                     t2.TimeoutEndForKind(K("Alice"), PenaltyKind.Griefing));
    }

    [Fact]
    public void Severity_combines_with_escalation()
    {
        var t = Tracker(
            griefBase: TimeSpan.FromMinutes(5),
            griefFactor: 2.0,
            griefMemory: TimeSpan.FromHours(24));

        t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0, severity: 1.0);
        t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0 + TimeSpan.FromMinutes(30), severity: 4.0);

        // Offense count = 2 → base 5min × factor^1 (=2) = 10min, then × severity 4 = 40min.
        Assert.Equal(T0 + TimeSpan.FromMinutes(30) + TimeSpan.FromMinutes(40),
                     t.TimeoutEndForKind(K("Alice"), PenaltyKind.Griefing));
    }

    [Fact]
    public void Snapshot_round_trips_severity()
    {
        var t = Tracker();
        t.RecordPenalty(K("Alice"), PenaltyKind.Griefing, T0, severity: 2.5);
        var snap = t.Snapshot();

        Assert.Single(snap);
        Assert.Equal(2.5, snap[0].Severity);

        var t2 = Tracker();
        t2.Rehydrate(snap);
        Assert.Equal(t.TimeoutEndForKind(K("Alice"), PenaltyKind.Griefing),
                     t2.TimeoutEndForKind(K("Alice"), PenaltyKind.Griefing));
    }
}
