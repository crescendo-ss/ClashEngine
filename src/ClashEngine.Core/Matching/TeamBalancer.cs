using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Matching;

/// <summary>
/// Pure team-balancing algorithm. Given an ordered list of candidates (typically longest-waiting
/// first), enumerates every distinct subset of size <see cref="MatchShape.TotalPlayers"/> that
/// includes the longest waiter (index 0), enumerates partitions of each subset, scores them,
/// and returns the highest-scoring partition.
/// </summary>
/// <remarks>
/// Pool size at the call site is bounded by <see cref="QueueDefinition.LookAheadWindow"/>. With
/// a 12-pool 4v4: C(11, 7) x 35 partitions ~= 11.5K evaluations -- sub-millisecond. Beyond ~16
/// the brute-force search starts to cost real time; the caller should keep LookAheadWindow within
/// reason for the team shape.
/// </remarks>
public sealed class TeamBalancer
{
    public BalanceResult? FindBest(
        IReadOnlyList<QueueEntry> candidates,
        MatchShape shape,
        IMatchQualityFunction quality,
        bool requireGroupsTogether = false)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(quality);

        int needed = shape.TotalPlayers;
        int poolSize = candidates.Count;
        if (poolSize < needed) return null;

        var pool = new QueueEntry[poolSize];
        for (int i = 0; i < poolSize; i++) pool[i] = candidates[i];

        BalanceResult? best = null;
        var teamRatingsBuffer = new List<List<Rating>>(shape.TeamCount);
        for (int t = 0; t < shape.TeamCount; t++)
            teamRatingsBuffer.Add(new List<Rating>(shape.PlayersPerTeam));

        // Pre-allocated subset buffer; index 0 is the longest waiter (always included).
        var subset = new int[needed];
        subset[0] = 0;

        foreach (var combo in CombinationsFromRange(1, poolSize, needed - 1))
        {
            for (int i = 0; i < combo.Length; i++) subset[i + 1] = combo[i];

            // Per-subset MaxOrdinalSpread check.
            if (shape.MaxOrdinalSpread is double cap && SubsetSpread(pool, subset) > cap)
                continue;

            foreach (var partition in EnumeratePartitions(needed, shape.TeamCount, shape.PlayersPerTeam))
            {
                if (requireGroupsTogether && SplitsAnyGroup(partition, subset, pool))
                    continue;

                for (int t = 0; t < shape.TeamCount; t++)
                {
                    teamRatingsBuffer[t].Clear();
                    for (int j = 0; j < shape.PlayersPerTeam; j++)
                        teamRatingsBuffer[t].Add(pool[subset[partition[t][j]]].Rating);
                }

                if (shape.MaxLiabilityGap is double maxGap && ViolatesLiabilityGap(teamRatingsBuffer, maxGap))
                    continue;

                double q = quality.Compute(teamRatingsBuffer);
                double imbalance = ComputeImbalance(teamRatingsBuffer);

                if (best is null || q > best.Quality)
                {
                    var teams = new IReadOnlyList<PlayerKey>[shape.TeamCount];
                    for (int t = 0; t < shape.TeamCount; t++)
                    {
                        var team = new PlayerKey[shape.PlayersPerTeam];
                        for (int j = 0; j < shape.PlayersPerTeam; j++)
                            team[j] = pool[subset[partition[t][j]]].Player;
                        teams[t] = team;
                    }
                    best = new BalanceResult(teams, q, imbalance);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Enumerates combinations of size <paramref name="k"/> from the half-open range
    /// <c>[start, end)</c>. Yields a single shared buffer; callers should copy if they need to
    /// retain it past the next iteration.
    /// </summary>
    private static IEnumerable<int[]> CombinationsFromRange(int start, int end, int k)
    {
        int rangeSize = end - start;
        if (k < 0 || k > rangeSize) yield break;
        if (k == 0) { yield return Array.Empty<int>(); yield break; }

        var buffer = new int[k];
        for (int i = 0; i < k; i++) buffer[i] = start + i;

        while (true)
        {
            yield return buffer;

            int j = k - 1;
            while (j >= 0 && buffer[j] == end - k + j) j--;
            if (j < 0) yield break;

            buffer[j]++;
            for (int i = j + 1; i < k; i++) buffer[i] = buffer[i - 1] + 1;
        }
    }

    private static double SubsetSpread(QueueEntry[] pool, int[] subset)
    {
        double minOrd = double.PositiveInfinity, maxOrd = double.NegativeInfinity;
        for (int i = 0; i < subset.Length; i++)
        {
            double o = pool[subset[i]].Rating.Ordinal;
            if (o < minOrd) minOrd = o;
            if (o > maxOrd) maxOrd = o;
        }
        return maxOrd - minOrd;
    }

    /// <summary>
    /// Returns true if any group ID appears across more than one team. <paramref name="partition"/>
    /// indexes into <paramref name="subset"/>, which indexes into <paramref name="pool"/>.
    /// </summary>
    private static bool SplitsAnyGroup(int[][] partition, int[] subset, QueueEntry[] pool)
    {
        Dictionary<GroupId, int>? groupTeam = null;
        for (int t = 0; t < partition.Length; t++)
        {
            for (int j = 0; j < partition[t].Length; j++)
            {
                if (pool[subset[partition[t][j]]].Group is not GroupId g) continue;
                groupTeam ??= new Dictionary<GroupId, int>();
                if (groupTeam.TryGetValue(g, out var existingTeam))
                {
                    if (existingTeam != t) return true;
                }
                else
                {
                    groupTeam[g] = t;
                }
            }
        }
        return false;
    }

    private static bool ViolatesLiabilityGap(IReadOnlyList<IReadOnlyList<Rating>> teams, double maxGap)
    {
        for (int t = 0; t < teams.Count; t++)
        {
            var team = teams[t];
            if (team.Count < 2) continue;

            double lowest = double.PositiveInfinity;
            double secondLowest = double.PositiveInfinity;
            for (int j = 0; j < team.Count; j++)
            {
                double o = team[j].Ordinal;
                if (o < lowest)
                {
                    secondLowest = lowest;
                    lowest = o;
                }
                else if (o < secondLowest)
                {
                    secondLowest = o;
                }
            }
            if (secondLowest - lowest > maxGap) return true;
        }
        return false;
    }

    private static double ComputeImbalance(IReadOnlyList<IReadOnlyList<Rating>> teams)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int t = 0; t < teams.Count; t++)
        {
            var team = teams[t];
            if (team.Count == 0) continue;
            double sum = 0;
            for (int j = 0; j < team.Count; j++) sum += team[j].Ordinal;
            double mean = sum / team.Count;
            if (mean < min) min = mean;
            if (mean > max) max = mean;
        }
        return double.IsPositiveInfinity(min) ? 0 : max - min;
    }

    /// <summary>
    /// Enumerates every distinct partition of <paramref name="n"/> indices into
    /// <paramref name="teamCount"/> teams of <paramref name="playersPerTeam"/>. Canonicalization:
    /// the smallest unassigned index always anchors the next team, eliminating team-label permutations.
    /// </summary>
    internal static IEnumerable<int[][]> EnumeratePartitions(int n, int teamCount, int playersPerTeam)
    {
        if (teamCount * playersPerTeam != n)
            throw new ArgumentException("teamCount * playersPerTeam must equal n.");

        var assigned = new bool[n];
        var teams = new int[teamCount][];
        for (int t = 0; t < teamCount; t++) teams[t] = new int[playersPerTeam];

        foreach (var partition in BuildTeam(0, assigned, teams, playersPerTeam))
            yield return partition;
    }

    private static IEnumerable<int[][]> BuildTeam(int teamIdx, bool[] assigned, int[][] teams, int playersPerTeam)
    {
        if (teamIdx == teams.Length)
        {
            var copy = new int[teams.Length][];
            for (int t = 0; t < teams.Length; t++)
            {
                copy[t] = new int[teams[t].Length];
                for (int j = 0; j < teams[t].Length; j++) copy[t][j] = teams[t][j];
            }
            yield return copy;
            yield break;
        }

        int anchor = -1;
        for (int i = 0; i < assigned.Length; i++)
            if (!assigned[i]) { anchor = i; break; }

        teams[teamIdx][0] = anchor;
        assigned[anchor] = true;

        foreach (var _ in PickPartners(teamIdx, 1, anchor + 1, assigned, teams, playersPerTeam))
        {
            foreach (var p in BuildTeam(teamIdx + 1, assigned, teams, playersPerTeam))
                yield return p;
        }

        assigned[anchor] = false;
    }

    private static IEnumerable<bool> PickPartners(
        int teamIdx, int slot, int startFrom, bool[] assigned, int[][] teams, int playersPerTeam)
    {
        if (slot == playersPerTeam)
        {
            yield return true;
            yield break;
        }

        for (int i = startFrom; i < assigned.Length; i++)
        {
            if (assigned[i]) continue;

            int remainingSlots = playersPerTeam - slot;
            int unassignedAfterI = 0;
            for (int j = i; j < assigned.Length; j++)
                if (!assigned[j]) unassignedAfterI++;
            if (unassignedAfterI < remainingSlots) break;

            teams[teamIdx][slot] = i;
            assigned[i] = true;

            foreach (var _ in PickPartners(teamIdx, slot + 1, i + 1, assigned, teams, playersPerTeam))
                yield return true;

            assigned[i] = false;
        }
    }
}
