using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Matches;

/// <summary>
/// Ends the match once <see cref="RegulationTime"/> has elapsed since
/// <see cref="ActiveMatch.StartedAt"/> AND there is a unique team leader by kill count. If the
/// time limit has elapsed but two or more teams are tied at the top, the match continues into
/// unlimited sudden-death overtime -- the next kill that creates a unique leader wins.
/// </summary>
/// <remarks>
/// <para>Scoring is by team kill count (same metric <see cref="KillCountEndPolicy"/> uses).
/// Final ranking is kills descending, with team index as the deterministic tiebreaker for any
/// non-leader teams that happen to be tied.</para>
///
/// <para>The match never ends in a tie: the only termination this policy returns is one with a
/// unique winner. Sudden death is unbounded by design -- the user requested "unlimited sudden
/// death overtime where the next kill wins."</para>
///
/// <para>The policy is stateless across calls; <see cref="CheckOutcome"/> reads
/// <see cref="ActiveMatch.StartedAt"/> and <see cref="ActiveMatch.KillsByTeam"/> live on every
/// invocation.</para>
/// </remarks>
public sealed class TimeLimitEndPolicy : IMatchEndPolicy
{
    public TimeLimitEndPolicy(TimeSpan regulationTime)
    {
        if (regulationTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(regulationTime), "Must be > 0.");
        RegulationTime = regulationTime;
    }

    public TimeSpan RegulationTime { get; }

    public MatchOutcome? CheckOutcome(ActiveMatch match, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(match);

        // Pre-Live: the match hasn't started counting yet.
        if (match.StartedAt is not { } startedAt) return null;

        var elapsed = at - startedAt;
        if (elapsed < RegulationTime) return null;

        // Past regulation. End only when there is a unique leader. With multiple teams tied at
        // the top, the match continues -- on each subsequent CheckOutcome (and after each kill,
        // since OnKill triggers TryFinish) this re-evaluates and ends as soon as the tie breaks.
        var kills = match.KillsByTeam;
        if (kills.Count == 0) return null;

        int maxScore = kills[0];
        int leaderCount = 1;
        for (int t = 1; t < kills.Count; t++)
        {
            if (kills[t] > maxScore)
            {
                maxScore = kills[t];
                leaderCount = 1;
            }
            else if (kills[t] == maxScore)
            {
                leaderCount++;
            }
        }
        if (leaderCount > 1) return null;   // sudden death continues

        // Unique leader -- finalize. Rank teams by score desc, ties broken by team index asc.
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
            int teamIdx = indices[i].idx;
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
