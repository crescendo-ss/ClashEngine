using System;

namespace ClashEngine.Core.Matches;

/// <summary>
/// Game-type-specific rules for declaring a match over. Implementations are queried after every
/// state-changing event and on each <see cref="ActiveMatch.Tick"/>.
/// </summary>
public interface IMatchEndPolicy
{
    /// <summary>
    /// Returns a <see cref="MatchOutcome"/> if the match should end now, or <see langword="null"/>
    /// if play should continue.
    /// </summary>
    MatchOutcome? CheckOutcome(ActiveMatch match, DateTimeOffset at);
}
