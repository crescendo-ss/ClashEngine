using System;
using System.Collections.Generic;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Matching;

/// <summary>
/// A simple match-quality function based on the spread of team-mean ordinals.
/// Quality = clamp(1 - (max_team_mean - min_team_mean) / normalizer, 0, 1).
/// </summary>
/// <remarks>
/// This is a pragmatic stand-in until OpenSkill's predict-draw quality is wired in.
/// It is monotonic in imbalance and produces values that are easy to reason about in tests.
/// </remarks>
public sealed class OrdinalSpreadQuality : IMatchQualityFunction
{
    private readonly double _normalizer;

    public OrdinalSpreadQuality(double normalizer = 50.0)
    {
        if (normalizer <= 0)
            throw new ArgumentOutOfRangeException(nameof(normalizer), "Must be positive.");
        _normalizer = normalizer;
    }

    public double Compute(IReadOnlyList<IReadOnlyList<Rating>> teams)
    {
        double minMean = double.PositiveInfinity;
        double maxMean = double.NegativeInfinity;

        for (int i = 0; i < teams.Count; i++)
        {
            var team = teams[i];
            if (team.Count == 0) continue;

            double sum = 0;
            for (int j = 0; j < team.Count; j++)
                sum += team[j].Ordinal;

            double mean = sum / team.Count;
            if (mean < minMean) minMean = mean;
            if (mean > maxMean) maxMean = mean;
        }

        if (double.IsPositiveInfinity(minMean)) return 0;

        double spread = maxMean - minMean;
        return Math.Clamp(1.0 - (spread / _normalizer), 0.0, 1.0);
    }
}
