namespace ClashEngine.Core.Matches;

/// <summary>Lifecycle state of an <see cref="ActiveMatch"/>.</summary>
public enum MatchState
{
    /// <summary>Players have been proposed but have not all entered the arena yet.</summary>
    Forming,

    /// <summary>All players are in the arena; the match is being played.</summary>
    Live,

    /// <summary>The end policy fired and the match has a normal outcome.</summary>
    Completed,

    /// <summary>Forming timed out: a player never showed. No rating updates for the match.</summary>
    Cancelled,

    /// <summary>A team has no active players remaining; the match ended without a normal outcome.</summary>
    Abandoned,
}

/// <summary>Per-player participation status within an <see cref="ActiveMatch"/>.</summary>
public enum PlayerStatus
{
    /// <summary>Has not entered the arena yet.</summary>
    Pending,

    /// <summary>In the arena, playing.</summary>
    Active,

    /// <summary>Temporarily out (specced, left arena, disconnected) -- return clock is running.</summary>
    InGrace,

    /// <summary>Grace window expired without return; the player is considered to have abandoned.</summary>
    Abandoned,
}
