using System;

namespace ClashEngine.Core.Queue;

/// <summary>
/// A center + radius box in tile coordinates. Primary use is one team's <b>respawn box</b>: the
/// values the orchestrator writes into a player's native Continuum <c>[Spawn]</c> Team0-3 client
/// settings (via <c>IClientSettings</c>) so the <em>client</em> respawns the player inside this
/// box after every death during a match (mirrors SubspaceServer's
/// <c>TeamVersusMatch.SendSpawnOverrides</c>; configured per game type as
/// <c>GameType&lt;i&gt;Team&lt;t&gt;SpawnCenter</c> + <c>GameType&lt;i&gt;Team&lt;t&gt;SpawnRadius</c>).
/// Also reused as the game type's <b>presence zone</b>
/// (<see cref="QueueDefinition.PresenceZone"/>): the box every team must keep at least one active
/// player inside or forfeit.
/// </summary>
/// <remarks>
/// <para><see cref="Center"/> is in <b>tiles</b> (the same coordinate convention as the start
/// location, <see cref="StartPoint"/>), so an operator can reuse one coordinate for both. The
/// native client setting is also in tiles, so <see cref="Center"/>'s <see cref="StartPoint.TileX"/> /
/// <see cref="StartPoint.TileY"/> are written to the client directly.</para>
/// <para><see cref="RadiusTiles"/> is in <b>tiles</b> (same unit as the start-drift budget and the
/// native 9-bit client radius field, which caps at 511 for respawn-box use). The client spawns the
/// ship at a random point within this radius of the center.</para>
/// <para>The respawn-box use is the in-match <em>respawn</em> location only; the <em>start</em>
/// location (a one-time server warp at match setup) is <see cref="StartPoint"/> / the queue's
/// start-set.</para>
/// </remarks>
public readonly record struct SpawnArea(StartPoint Center, int RadiusTiles)
{
    /// <summary>
    /// True iff the tile coordinate lies within the box: at most <see cref="RadiusTiles"/> from
    /// <see cref="Center"/> on each axis independently (a square of side <c>2r+1</c> tiles, not a
    /// circle -- matching how the native client treats the spawn radius field).
    /// </summary>
    public bool Contains(int tileX, int tileY) =>
        Math.Abs(tileX - Center.X) <= RadiusTiles && Math.Abs(tileY - Center.Y) <= RadiusTiles;
}
