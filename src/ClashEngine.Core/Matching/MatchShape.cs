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

    /// <summary>
    /// Reject any match roster whose players span more than this in raw <c>mu</c>. The highest-mu
    /// player in the candidate set sets the bar: every other player must be within
    /// <see cref="MaxMuSpread"/> of them to be eligible for the match. Unlike the ordinal caps
    /// (which fold sigma into a conservative estimate), this gates on the skill estimate itself, so
    /// a strong-but-uncertain player still raises the floor. Checked on the candidate subset before
    /// teams are formed, so a too-weak player is never pulled into a much stronger match at all;
    /// when no subset clears the cap, the balancer forms no match and the pool keeps waiting.
    /// <see langword="null"/> = no cap.
    /// </summary>
    public double? MaxMuSpread { get; }

    public MatchShape(
        int teamCount,
        int playersPerTeam,
        double? maxOrdinalSpread = null,
        double? maxLiabilityGap = null,
        double? maxMuSpread = null)
    {
        if (teamCount < 2)
            throw new ArgumentOutOfRangeException(nameof(teamCount), "Need at least 2 teams.");
        if (playersPerTeam < 1)
            throw new ArgumentOutOfRangeException(nameof(playersPerTeam), "Need at least 1 player per team.");
        if (maxOrdinalSpread is { } cap && cap < 0)
            throw new ArgumentOutOfRangeException(nameof(maxOrdinalSpread), "Must be non-negative.");
        if (maxLiabilityGap is { } gap && gap < 0)
            throw new ArgumentOutOfRangeException(nameof(maxLiabilityGap), "Must be non-negative.");
        if (maxMuSpread is { } muSpread && muSpread < 0)
            throw new ArgumentOutOfRangeException(nameof(maxMuSpread), "Must be non-negative.");

        TeamCount = teamCount;
        PlayersPerTeam = playersPerTeam;
        MaxOrdinalSpread = maxOrdinalSpread;
        MaxLiabilityGap = maxLiabilityGap;
        MaxMuSpread = maxMuSpread;
    }

    public int TotalPlayers => TeamCount * PlayersPerTeam;
}
