using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Matches;

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
    GameTypeId GameType,
    IReadOnlyList<RankedTeam> RankedTeams,
    IReadOnlyList<PlayerKey> AbandonedBy,
    MatchState FinalState,
    DateTimeOffset EndedAt,
    IReadOnlyDictionary<PlayerKey, PlayerOutcomeStats>? PlayerStats = null,
    int? LivesPerPlayer = null,
    TimeSpan? Duration = null);
