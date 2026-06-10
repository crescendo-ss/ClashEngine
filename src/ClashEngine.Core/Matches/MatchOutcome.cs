using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Matches;

// GameType identifier is the registry name (string), matching the v5 match-upload schema.

/// <summary>One team's slot in a final ranking. Lower <see cref="Rank"/> = better placement (1 = winner).</summary>
public sealed record RankedTeam(int Rank, IReadOnlyList<PlayerKey> Players, int Score);

/// <summary>
/// Per-player contribution data carried alongside a <see cref="MatchOutcome"/>. Used by the rating
/// updater to weight individual rating deltas: high-kill / long-survival winners get a bigger
/// boost; long-survival losers absorb a smaller penalty (being eliminated last is "less their
/// fault").
/// </summary>
public sealed record PlayerOutcomeStats(int Kills, TimeSpan TimeAlive);

/// <summary>
/// How a Completed match's outcome was decided, beyond what <see cref="MatchState"/> can express.
/// Engine-internal flavor used by adapters to tailor the end-of-match announcement; deliberately
/// NOT part of the upload wire shape (<c>schema/match.schema.json</c> carries only the final
/// state). (Named to avoid colliding with SS.Matchmaking's unrelated <c>MatchEndReason</c>.)
/// </summary>
public enum MatchOutcomeReason
{
    /// <summary>End policy fired (kill target / time limit) or a team-collapse forfeit.</summary>
    Standard,

    /// <summary>A team failed to keep at least one active player inside the game type's presence
    /// zone for <see cref="ActiveMatch.PresenceZoneTimeout"/>; the violator is ranked last.</summary>
    ZoneForfeit,
}

/// <summary>
/// Final outcome of a match suitable for handing to a rating updater. Contains the ranked teams
/// (in any order) plus the players who abandoned (for queue-timeout penalties).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PlayerStats"/>, <see cref="LivesPerPlayer"/>, and <see cref="Duration"/> are optional
/// enrichments so older test fixtures and external callers that only care about ranks remain
/// compatible. When all three are present, <see cref="Ratings.RatingUpdater"/> applies
/// margin-of-victory scaling and per-player OpenSkill weights; when any are missing it falls back
/// to uniform weights.
/// </para>
/// </remarks>
public sealed record MatchOutcome(
    Guid MatchId,
    string GameType,
    IReadOnlyList<RankedTeam> RankedTeams,
    IReadOnlyList<PlayerKey> AbandonedBy,
    MatchState FinalState,
    DateTimeOffset EndedAt,
    IReadOnlyDictionary<PlayerKey, PlayerOutcomeStats>? PlayerStats = null,
    int? LivesPerPlayer = null,
    TimeSpan? Duration = null,
    MatchOutcomeReason EndReason = MatchOutcomeReason.Standard);
