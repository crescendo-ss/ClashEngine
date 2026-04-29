namespace ClashEngine.Core.Penalties;

/// <summary>Why a queue-timeout was applied.</summary>
public enum PenaltyKind
{
    /// <summary>Player left/disconnected/specced past the in-match grace window.</summary>
    Abandonment = 0,

    /// <summary>Player completed the match but was flagged by a per-game-mode griefing heuristic.</summary>
    Griefing = 1,

    /// <summary>
    /// Player was eliminated from a limited-lives match (lives reached 0). Brief cooldown
    /// before they can queue again -- discourages "dying on purpose to leave the match early."
    /// </summary>
    EliminationCooldown = 2,

    /// <summary>
    /// Player failed the staging-phase readiness check after a match was proposed (didn't
    /// move/fire in the warmup window, so the match was cancelled). Distinct from
    /// <see cref="Abandonment"/> because the match never actually started -- the punishment
    /// starts mild and escalates with repetition.
    /// </summary>
    StagingAfk = 3,
}
