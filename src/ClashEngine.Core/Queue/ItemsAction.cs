namespace ClashEngine.Core.Queue;

/// <summary>
/// What the orchestrator does to a player's stockpilable item counts (bursts, repels, thors,
/// bricks, decoys, rockets, portals) when it (re-)places them on a ship mid-match -- e.g. on
/// <c>?return</c>. Mirrors the same-named concept in <c>SS.Matchmaking.TeamVersusMatch</c>.
/// </summary>
public enum ItemsAction
{
    /// <summary>Leave the freshly-spawned ship's loadout untouched (Continuum's default counts).</summary>
    Full,

    /// <summary>Deduct items from the spawn-fresh loadout back down to whatever the player had at the
    /// moment they last left the match (i.e. their pre-spec inventory). Closes the free-reload
    /// loophole where a self-spec / <c>?return</c> would refill bursts and repels.</summary>
    Restore,

    /// <summary>Zero out every stockpilable item, regardless of what the player had before leaving.</summary>
    Burn,
}
