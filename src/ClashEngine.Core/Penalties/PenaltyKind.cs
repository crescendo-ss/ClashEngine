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
}
