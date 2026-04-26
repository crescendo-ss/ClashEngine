namespace ClashEngine.Core.Stats;

/// <summary>
/// Reason a life ended. A life starts at spawn and ends only via these transitions -- ship and
/// freq changes are not life transitions in ClashEngine, and players cannot suicide.
/// </summary>
public enum LifeEndReason
{
    KilledByEnemy,
    KilledByTeammate,
    LeftMatch,
    MatchEnded,
}
