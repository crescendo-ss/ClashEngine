using System;

namespace ClashEngine.Core.Matching;

/// <summary>
/// The structural shape of a match: how many teams of how many players, plus optional caps.
/// </summary>
public sealed record MatchShape
{
    public int TeamCount { get; }
    public int PlayersPerTeam { get; }

    /// <summary>Reject any partition whose candidate pool spans more than this in ordinal rating.</summary>
    public double? MaxOrdinalSpread { get; }

    /// <summary>
    /// Reject any partition where some team's lowest-rated player is more than this many ordinal
    /// points below the team's second-lowest. Catches the "liability" pattern where a single low
    /// player ruins a team. <see langword="null"/> = no cap (suitable for casual queues).
    /// </summary>
    public double? MaxLiabilityGap { get; }

    public MatchShape(
        int teamCount,
        int playersPerTeam,
        double? maxOrdinalSpread = null,
        double? maxLiabilityGap = null)
    {
        if (teamCount < 2)
            throw new ArgumentOutOfRangeException(nameof(teamCount), "Need at least 2 teams.");
        if (playersPerTeam < 1)
            throw new ArgumentOutOfRangeException(nameof(playersPerTeam), "Need at least 1 player per team.");
        if (maxOrdinalSpread is { } cap && cap < 0)
            throw new ArgumentOutOfRangeException(nameof(maxOrdinalSpread), "Must be non-negative.");
        if (maxLiabilityGap is { } gap && gap < 0)
            throw new ArgumentOutOfRangeException(nameof(maxLiabilityGap), "Must be non-negative.");

        TeamCount = teamCount;
        PlayersPerTeam = playersPerTeam;
        MaxOrdinalSpread = maxOrdinalSpread;
        MaxLiabilityGap = maxLiabilityGap;
    }

    public int TotalPlayers => TeamCount * PlayersPerTeam;
}
