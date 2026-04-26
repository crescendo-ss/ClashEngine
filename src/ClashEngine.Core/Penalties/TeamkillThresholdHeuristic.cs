using System;
using System.Collections.Generic;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Penalties;

/// <summary>
/// Flags any player whose teamkill count strictly exceeds <see cref="Threshold"/>. Severity
/// scales linearly with how many teamkills the player committed: the smallest flagged value
/// (<see cref="Threshold"/> + 1 teamkills) yields severity 1.0; double that yields severity 2.0;
/// etc., capped at <see cref="SeverityCap"/>.
/// </summary>
public sealed class TeamkillThresholdHeuristic : IGriefingHeuristic
{
    public TeamkillThresholdHeuristic(int threshold, double severityCap = 5.0)
    {
        if (threshold < 0) throw new ArgumentOutOfRangeException(nameof(threshold), "Must be >= 0.");
        if (severityCap < 1.0) throw new ArgumentOutOfRangeException(nameof(severityCap), "Must be >= 1.");
        Threshold = threshold;
        SeverityCap = severityCap;
    }

    public int Threshold { get; }
    public double SeverityCap { get; }

    public IReadOnlyList<GriefingFlag> Evaluate(ActiveMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        List<GriefingFlag>? flags = null;
        foreach (var kvp in match.TeamkillsByPlayer)
        {
            if (kvp.Value <= Threshold) continue;

            double severity = Math.Min(SeverityCap, (double)kvp.Value / (Threshold + 1));
            (flags ??= new List<GriefingFlag>()).Add(
                new GriefingFlag(kvp.Key, $"{kvp.Value} teamkills (threshold {Threshold})", severity));
        }
        return flags ?? (IReadOnlyList<GriefingFlag>)Array.Empty<GriefingFlag>();
    }
}
