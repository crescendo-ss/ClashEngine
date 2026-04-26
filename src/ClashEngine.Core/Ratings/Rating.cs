using System;

namespace ClashEngine.Core.Ratings;

/// <summary>
/// A skill rating for one player in one game type. Mirrors OpenSkill's <c>(mu, sigma)</c>
/// shape with bookkeeping fields for persistence.
/// </summary>
public readonly record struct Rating(double Mu, double Sigma, uint GamesPlayed, DateTimeOffset LastSeen)
{
    /// <summary>OpenSkill default starting values: <c>mu = 25</c>, <c>sigma = 25/3</c>.</summary>
    public static Rating Default { get; } = new(25.0, 25.0 / 3.0, 0, default);

    /// <summary>Conservative skill estimate: <c>mu - 3*sigma</c>. Used for matchmaking ordering.</summary>
    public double Ordinal => Mu - 3.0 * Sigma;

    public Rating WithGameRecorded(double newMu, double newSigma, DateTimeOffset at) =>
        new(newMu, newSigma, GamesPlayed + 1, at);
}
