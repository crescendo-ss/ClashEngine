using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;

namespace ClashEngine.Core.Penalties;

/// <summary>
/// Runs several <see cref="IGriefingHeuristic"/>s over the same match and merges their flags.
/// A player nominated by more than one child keeps only the highest-severity flag --
/// <c>FlagGriefers</c> records one penalty per returned flag, so duplicate nominations for the
/// same player would double-penalize them (and overwrite each other's pending-veto entry).
/// </summary>
public sealed class CompositeGriefingHeuristic : IGriefingHeuristic
{
    public CompositeGriefingHeuristic(params IGriefingHeuristic[] heuristics)
    {
        ArgumentNullException.ThrowIfNull(heuristics);
        if (heuristics.Length == 0)
            throw new ArgumentException("At least one heuristic is required.", nameof(heuristics));
        foreach (var h in heuristics)
            ArgumentNullException.ThrowIfNull(h, nameof(heuristics));
        Heuristics = heuristics;
    }

    public IReadOnlyList<IGriefingHeuristic> Heuristics { get; }

    public IReadOnlyList<GriefingFlag> Evaluate(ActiveMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        List<GriefingFlag>? merged = null;
        Dictionary<PlayerKey, int>? indexByPlayer = null;
        foreach (var heuristic in Heuristics)
        {
            var flags = heuristic.Evaluate(match);
            for (int i = 0; i < flags.Count; i++)
            {
                var flag = flags[i];
                merged ??= new List<GriefingFlag>();
                indexByPlayer ??= new Dictionary<PlayerKey, int>();

                if (indexByPlayer.TryGetValue(flag.Player, out int existing))
                {
                    if (flag.Severity > merged[existing].Severity)
                        merged[existing] = flag;
                }
                else
                {
                    indexByPlayer[flag.Player] = merged.Count;
                    merged.Add(flag);
                }
            }
        }
        return merged ?? (IReadOnlyList<GriefingFlag>)Array.Empty<GriefingFlag>();
    }
}
