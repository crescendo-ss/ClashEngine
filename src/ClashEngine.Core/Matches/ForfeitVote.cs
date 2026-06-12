namespace ClashEngine.Core.Matches;

/// <summary>
/// Outcome of a single <c>?forfeit</c> vote attempt (see <see cref="ActiveMatch.TryForfeit"/>).
/// </summary>
public enum ForfeitVoteResult
{
    /// <summary>The team's first forfeit vote registered; teammates must now agree.</summary>
    Requested,

    /// <summary>A subsequent vote registered; the team is not yet unanimous.</summary>
    Agreed,

    /// <summary>This vote made the team unanimous: the team forfeited. For a two-team match the
    /// match has already finalized by the time the caller sees this.</summary>
    Completed,

    /// <summary>The player already has a (sticky) forfeit vote registered.</summary>
    AlreadyVoted,

    /// <summary>The player has exhausted their lives; the vote belongs to the remaining
    /// (non-eliminated) teammates.</summary>
    Eliminated,

    /// <summary>The match isn't Live yet (or has already ended) -- there is nothing to forfeit.</summary>
    NotLive,

    /// <summary>The player isn't rostered in an active match.</summary>
    NotInMatch,
}

/// <summary>
/// Result of a forfeit vote attempt: what happened, plus the team's current tally.
/// <see cref="Votes"/> counts registered votes from currently-eligible voters and
/// <see cref="Needed"/> is the eligible-voter denominator (non-eliminated, non-abandoned team
/// members), so a "(2/4)" progress message can be formatted directly. Both are 0 when the vote
/// didn't resolve to a team (<see cref="ForfeitVoteResult.NotInMatch"/>,
/// <see cref="ForfeitVoteResult.NotLive"/>).
/// </summary>
public readonly record struct ForfeitVote(ForfeitVoteResult Result, int Votes, int Needed);
