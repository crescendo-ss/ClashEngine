using System;
using System.Collections.Generic;
using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Matches;

/// <summary>One team's slot in a final ranking. Lower <see cref="Rank"/> = better placement (1 = winner).</summary>
public sealed record RankedTeam(int Rank, IReadOnlyList<PlayerKey> Players, int Score);

/// <summary>
/// Final outcome of a match suitable for handing to a rating updater. Contains the ranked teams
/// (in any order) plus the players who abandoned (for queue-timeout penalties).
/// </summary>
public sealed record MatchOutcome(
    Guid MatchId,
    GameTypeId GameType,
    IReadOnlyList<RankedTeam> RankedTeams,
    IReadOnlyList<PlayerKey> AbandonedBy,
    MatchState FinalState,
    DateTimeOffset EndedAt);
