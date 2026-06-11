using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Tests.Penalties;

public class GriefingHeuristicSelectorTests
{
    private static PlayerKey K(string n) => new(n);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- defaults: both detectors are opt-in ----

    [Fact]
    public void Nothing_enabled_yields_no_heuristic()
    {
        var heuristic = GriefingHeuristicSelector.Build(defaultTeamkillThreshold: 2);

        Assert.IsType<NoGriefingHeuristic>(heuristic);
    }

    [Fact]
    public void Quick_1v1_loss_is_not_flagged_by_default()
    {
        // The regression scenario: a 1-life 1v1 where the loser burns their life in seconds.
        // Griefing detectors are opt-in, so by default losing fast carries no penalty.
        var startedAt = T0.AddSeconds(1);
        var m = new ActiveMatch(
            Guid.NewGuid(), "1v1",
            new IReadOnlyList<PlayerKey>[] { new[] { K("A") }, new[] { K("B") } },
            new KillCountEndPolicy(100),
            joinTimeout: TimeSpan.FromMinutes(1),
            graceWindow: TimeSpan.FromSeconds(30),
            proposedAt: T0,
            livesPerPlayer: 1);
        foreach (var team in m.Teams)
            foreach (var p in team)
                m.OnPlayerJoined(p, startedAt);
        m.MarkLive(startedAt);
        m.OnKill(K("A"), K("B"), startedAt.AddSeconds(15));   // B loses their only life at +15s

        var heuristic = GriefingHeuristicSelector.Build(defaultTeamkillThreshold: 2);

        Assert.Empty(heuristic.Evaluate(m));
    }

    // ---- enabling each detector ----

    [Fact]
    public void Early_exit_enabled_uses_the_two_minute_default()
    {
        var heuristic = GriefingHeuristicSelector.Build(
            defaultTeamkillThreshold: 2, earlyExitEnabled: true);

        var earlyExit = Assert.IsType<EarlyExitHeuristic>(heuristic);
        Assert.Equal(TimeSpan.FromMinutes(2), earlyExit.MinimumDuration);
    }

    [Fact]
    public void Teamkill_enabled_uses_the_supplied_default_threshold()
    {
        var heuristic = GriefingHeuristicSelector.Build(
            defaultTeamkillThreshold: 3, teamkillEnabled: true);

        var teamkill = Assert.IsType<TeamkillThresholdHeuristic>(heuristic);
        Assert.Equal(3, teamkill.Threshold);
    }

    [Fact]
    public void Both_detectors_enabled_yields_a_composite_of_both()
    {
        var heuristic = GriefingHeuristicSelector.Build(
            defaultTeamkillThreshold: 2, earlyExitEnabled: true, teamkillEnabled: true);

        var composite = Assert.IsType<CompositeGriefingHeuristic>(heuristic);
        Assert.Equal(2, composite.Heuristics.Count);
        Assert.Contains(composite.Heuristics, h => h is EarlyExitHeuristic);
        Assert.Contains(composite.Heuristics, h => h is TeamkillThresholdHeuristic);
    }

    // ---- knob plumbing ----

    [Fact]
    public void Explicit_early_exit_minimum_duration_overrides_the_default()
    {
        var heuristic = GriefingHeuristicSelector.Build(
            defaultTeamkillThreshold: 2,
            earlyExitEnabled: true,
            earlyExitMinimumDuration: TimeSpan.FromSeconds(30));

        var earlyExit = Assert.IsType<EarlyExitHeuristic>(heuristic);
        Assert.Equal(TimeSpan.FromSeconds(30), earlyExit.MinimumDuration);
    }

    [Fact]
    public void Explicit_teamkill_threshold_overrides_the_default()
    {
        var heuristic = GriefingHeuristicSelector.Build(
            defaultTeamkillThreshold: 2,
            teamkillEnabled: true,
            teamkillThreshold: 7);

        var teamkill = Assert.IsType<TeamkillThresholdHeuristic>(heuristic);
        Assert.Equal(7, teamkill.Threshold);
    }

    [Fact]
    public void Knob_values_are_ignored_while_their_detector_is_disabled()
    {
        var heuristic = GriefingHeuristicSelector.Build(
            defaultTeamkillThreshold: 2,
            earlyExitMinimumDuration: TimeSpan.FromSeconds(30),
            teamkillThreshold: 7);

        Assert.IsType<NoGriefingHeuristic>(heuristic);
    }

    // ---- validation ----

    [Fact]
    public void Negative_default_teamkill_threshold_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GriefingHeuristicSelector.Build(defaultTeamkillThreshold: -1));
    }
}
