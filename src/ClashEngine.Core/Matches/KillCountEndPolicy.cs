using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Matches;

/// <summary>
/// Ends the match when any team reaches <see cref="TargetKills"/>. Teams are ranked by kill count
/// (descending), ties broken by team index (lower team index ranks better).
/// </summary>
public sealed class KillCountEndPolicy : IMatchEndPolicy
{
    public KillCountEndPolicy(int targetKills)
    {
        if (targetKills < 1) throw new ArgumentOutOfRangeException(nameof(targetKills), "Must be >= 1.");
        TargetKills = targetKills;
    }

    public int TargetKills { get; }

    public MatchOutcome? CheckOutcome(ActiveMatch match, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(match);

        bool reached = false;
        var kills = match.KillsByTeam;
        for (int t = 0; t < kills.Count; t++)
            if (kills[t] >= TargetKills) { reached = true; break; }

        if (!reached) return null;

        // Rank teams by score desc, then by team index asc to break ties deterministically.
        var indices = new (int idx, int score)[kills.Count];
        for (int t = 0; t < kills.Count; t++) indices[t] = (t, kills[t]);

        Array.Sort(indices, (a, b) =>
        {
            int byScore = b.score.CompareTo(a.score);
            return byScore != 0 ? byScore : a.idx.CompareTo(b.idx);
        });

        var ranked = new RankedTeam[kills.Count];
        var abandoners = new List<PlayerKey>();
        for (int i = 0; i < indices.Length; i++)
        {
            var teamIdx = indices[i].idx;
            ranked[i] = new RankedTeam(i + 1, match.Teams[teamIdx], indices[i].score);
            for (int j = 0; j < match.Teams[teamIdx].Count; j++)
            {
                var p = match.Teams[teamIdx][j];
                if (match.GetStatus(p) == PlayerStatus.Abandoned)
                    abandoners.Add(p);
            }
        }

        return new MatchOutcome(match.MatchId, match.GameType, ranked, abandoners, MatchState.Completed, at);
    }
}
