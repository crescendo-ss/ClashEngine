using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Matching;

/// <summary>
/// Pure team-balancing algorithm. Given an ordered list of candidates (typically longest-waiting
/// first), enumerates distinct subsets of size <see cref="MatchShape.TotalPlayers"/>, enumerates
/// partitions of each subset, scores them, and returns the highest-scoring partition. By default
/// every considered subset includes the longest waiter (index 0) for FIFO fairness; pass
/// <c>requireLongestWaiter: false</c> to let the search pass the head over in favor of a
/// better-balanced subset (see <see cref="ClashEngine.Core.Queue.QueueDefinition.AlwaysChooseLongestWaiter"/>).
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
        bool requireGroupsTogether = false,
        IReadOnlyDictionary<GroupId, int>? fullGroupSizes = null,
        bool requireLongestWaiter = true,
        bool enforceMuSpread = true)
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

        // Subset buffer. When requireLongestWaiter is set, index 0 (the longest waiter) is pinned
        // into every subset and we pick the other needed-1 from [1, poolSize); otherwise we pick
        // all needed freely from [0, poolSize), letting the head be passed over for better balance.
        var subset = new int[needed];
        int comboStart = requireLongestWaiter ? 1 : 0;
        int comboCount = requireLongestWaiter ? needed - 1 : needed;
        int subsetOffset = requireLongestWaiter ? 1 : 0;
        if (requireLongestWaiter) subset[0] = 0;

        foreach (var combo in CombinationsFromRange(comboStart, poolSize, comboCount))
        {
            for (int i = 0; i < combo.Length; i++) subset[subsetOffset + i] = combo[i];

            // Per-subset MaxOrdinalSpread check.
            if (shape.MaxOrdinalSpread is double cap && SubsetSpread(pool, subset) > cap)
                continue;

            // Per-subset MaxMuSpread check: the highest-mu player in the roster sets the bar, and
            // every other selected player must be within MaxMuSpread of them. A subset that spans
            // too far in raw mu is not an eligible match roster at all, so a too-weak player is
            // never pulled into a much stronger match -- independent of how teams would be split.
            // enforceMuSpread=false is the caller's relaxation escape hatch (see the Matcher's
            // RelaxTime-driven fallback): it forgoes this cap so a long-waiting pool can still form.
            if (enforceMuSpread && shape.MaxMuSpread is double muCap && SubsetMuSpread(pool, subset) > muCap)
                continue;

            // Party integrity: a group is selected all-or-nothing. Skip any subset that includes
            // some-but-not-all of a group's queued members. This is independent of the same-team
            // SplitsAnyGroup rule below -- splitting a fully-selected party across teams is allowed,
            // but pulling in part of a party while the rest stay in the queue is not.
            if (fullGroupSizes is not null && SubsetPartiallySelectsGroup(subset, pool, fullGroupSizes))
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
    /// The subset's spread in raw <c>mu</c>: highest-mu player minus lowest-mu player. Gates on
    /// the skill estimate itself (not ordinal), so a strong-but-uncertain player still sets a high
    /// bar -- see <see cref="MatchShape.MaxMuSpread"/>.
    /// </summary>
    private static double SubsetMuSpread(QueueEntry[] pool, int[] subset)
    {
        double minMu = double.PositiveInfinity, maxMu = double.NegativeInfinity;
        for (int i = 0; i < subset.Length; i++)
        {
            double mu = pool[subset[i]].Rating.Mu;
            if (mu < minMu) minMu = mu;
            if (mu > maxMu) maxMu = mu;
        }
        return maxMu - minMu;
    }

    /// <summary>
    /// Returns true if <paramref name="subset"/> includes some-but-not-all of any group's queued
    /// members -- i.e. the subset would partially select a party. <paramref name="fullGroupSizes"/>
    /// counts each group across the WHOLE queue snapshot (not just the candidate pool), so a group
    /// with a member beyond the lookahead pool can never reach its full size within a pool subset
    /// and is rejected here too -- preventing a partial pull-in across the lookahead boundary.
    /// </summary>
    private static bool SubsetPartiallySelectsGroup(
        int[] subset, QueueEntry[] pool, IReadOnlyDictionary<GroupId, int> fullGroupSizes)
    {
        Dictionary<GroupId, int>? selected = null;
        for (int i = 0; i < subset.Length; i++)
        {
            if (pool[subset[i]].Group is not GroupId g) continue;
            selected ??= new Dictionary<GroupId, int>();
            selected[g] = selected.TryGetValue(g, out var c) ? c + 1 : 1;
        }
        if (selected is null) return false;

        foreach (var kvp in selected)
            if (!fullGroupSizes.TryGetValue(kvp.Key, out var full) || kvp.Value != full)
                return true;
        return false;
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
