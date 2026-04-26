using System;
using System.Collections.Generic;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Matching;

/// <summary>
/// Quality function that combines two penalties:
/// <list type="bullet">
/// <item>inter-team mean ordinal spread (do the teams have similar overall skill?)</item>
/// <item>worst single-player drift from their team mean (is one player dragging a team down?)</item>
/// </list>
/// Both penalties are normalized and clamped: <c>quality = clamp(1 - alpha.meanSpread/N1 - beta.worstDrift/N2, 0, 1)</c>.
/// </summary>
/// <remarks>
/// Catches the failure mode that <see cref="OrdinalSpreadQuality"/> misses, where a low-rated
/// player on each team yields equal team means but produces a lopsided in-game experience.
/// </remarks>
public sealed class BalancedTeamQuality : IMatchQualityFunction
{
    public BalancedTeamQuality(
        double meanSpreadNormalizer = 50.0,
        double playerDriftNormalizer = 25.0,
        double meanWeight = 0.5,
        double driftWeight = 0.5)
    {
        if (meanSpreadNormalizer <= 0)
            throw new ArgumentOutOfRangeException(nameof(meanSpreadNormalizer), "Must be positive.");
        if (playerDriftNormalizer <= 0)
            throw new ArgumentOutOfRangeException(nameof(playerDriftNormalizer), "Must be positive.");
        if (meanWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(meanWeight), "Must be non-negative.");
        if (driftWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(driftWeight), "Must be non-negative.");

        MeanSpreadNormalizer = meanSpreadNormalizer;
        PlayerDriftNormalizer = playerDriftNormalizer;
        MeanWeight = meanWeight;
        DriftWeight = driftWeight;
    }

    public double MeanSpreadNormalizer { get; }
    public double PlayerDriftNormalizer { get; }
    public double MeanWeight { get; }
    public double DriftWeight { get; }

    public double Compute(IReadOnlyList<IReadOnlyList<Rating>> teams)
    {
        if (teams.Count == 0) return 0;

        double minMean = double.PositiveInfinity, maxMean = double.NegativeInfinity;
        double worstDrift = 0;

        for (int t = 0; t < teams.Count; t++)
        {
            var team = teams[t];
            if (team.Count == 0) continue;

            double sum = 0;
            for (int j = 0; j < team.Count; j++) sum += team[j].Ordinal;
            double mean = sum / team.Count;

            if (mean < minMean) minMean = mean;
            if (mean > maxMean) maxMean = mean;

            for (int j = 0; j < team.Count; j++)
            {
                double drift = Math.Abs(team[j].Ordinal - mean);
                if (drift > worstDrift) worstDrift = drift;
            }
        }

        if (double.IsPositiveInfinity(minMean)) return 0;

        double meanPenalty = MeanWeight * (maxMean - minMean) / MeanSpreadNormalizer;
        double driftPenalty = DriftWeight * worstDrift / PlayerDriftNormalizer;
        return Math.Clamp(1.0 - meanPenalty - driftPenalty, 0.0, 1.0);
    }
}
