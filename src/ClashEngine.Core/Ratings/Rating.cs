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

    /// <summary>
    /// True when this row was written by a pull that confirmed the stats server has no
    /// rating for this <c>(player, gameType)</c> pair -- a "brand new player" marker. The
    /// values are <see cref="Default"/>; the flag is the disambiguator between "we asked,
    /// server was empty" and "we never asked". Cleared automatically when the player
    /// finishes a match (<see cref="WithGameRecorded"/> returns a fresh row with
    /// <see cref="IsNew"/> defaulting to false).
    /// </summary>
    /// <remarks>
    /// Not persisted across process restarts (the on-disk blob doesn't carry the flag).
    /// After a restart, <see cref="RatingSyncCoordinator"/>'s next connect-pull re-establishes
    /// the marker if the server is still empty.
    /// </remarks>
    public bool IsNew { get; init; }

    public Rating WithGameRecorded(double newMu, double newSigma, DateTimeOffset at) =>
        new(newMu, newSigma, GamesPlayed + 1, at);
}
