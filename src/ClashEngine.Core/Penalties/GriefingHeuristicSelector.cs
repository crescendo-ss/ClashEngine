using System;
using System.Collections.Generic;

namespace ClashEngine.Core.Penalties;

/// <summary>
/// Builds the griefing detector for one queue from the per-game-type griefing knobs. Both
/// detectors are opt-in: a game type that doesn't explicitly enable <see cref="EarlyExitHeuristic"/>
/// or <see cref="TeamkillThresholdHeuristic"/> gets <see cref="NoGriefingHeuristic"/> (no automated
/// griefing penalties). With both enabled the heuristics run together via
/// <see cref="CompositeGriefingHeuristic"/>.
/// </summary>
public static class GriefingHeuristicSelector
{
    /// <summary>Default early-exit minimum participation time when the knob is unset.</summary>
    public static readonly TimeSpan DefaultEarlyExitMinimumDuration = TimeSpan.FromMinutes(2);

    /// <param name="defaultTeamkillThreshold">Teamkill threshold used when
    /// <paramref name="teamkillThreshold"/> is unset (the queue preset's default).</param>
    /// <param name="earlyExitEnabled">Whether the early-exit detector runs. Note it only ever
    /// fires in limited-lives matches, and is intended for team shapes -- in a 1v1 (or other
    /// solo-team mode) it flags players for simply losing fast.</param>
    /// <param name="earlyExitMinimumDuration">Early-exit minimum participation time;
    /// <see langword="null"/> = <see cref="DefaultEarlyExitMinimumDuration"/>.</param>
    /// <param name="teamkillEnabled">Whether the teamkill-threshold detector runs.</param>
    /// <param name="teamkillThreshold">Teamkill count a player must strictly exceed to be flagged;
    /// <see langword="null"/> = <paramref name="defaultTeamkillThreshold"/>.</param>
    public static IGriefingHeuristic Build(
        int defaultTeamkillThreshold,
        bool earlyExitEnabled = false,
        TimeSpan? earlyExitMinimumDuration = null,
        bool teamkillEnabled = false,
        int? teamkillThreshold = null)
    {
        if (defaultTeamkillThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(defaultTeamkillThreshold), "Must be >= 0.");

        var parts = new List<IGriefingHeuristic>(2);
        if (earlyExitEnabled)
            parts.Add(new EarlyExitHeuristic(
                minimumDuration: earlyExitMinimumDuration ?? DefaultEarlyExitMinimumDuration));
        if (teamkillEnabled)
            parts.Add(new TeamkillThresholdHeuristic(
                threshold: teamkillThreshold ?? defaultTeamkillThreshold));

        return parts.Count switch
        {
            0 => NoGriefingHeuristic.Instance,
            1 => parts[0],
            _ => new CompositeGriefingHeuristic(parts.ToArray()),
        };
    }
}
