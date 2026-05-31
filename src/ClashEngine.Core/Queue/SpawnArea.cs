namespace ClashEngine.Core.Queue;

/// <summary>
/// One team's <b>respawn box</b>: the center + radius the orchestrator writes into a player's
/// native Continuum <c>[Spawn]</c> Team0-3 client settings (via <c>IClientSettings</c>) so the
/// <em>client</em> respawns the player inside this box after every death during a match. Mirrors
/// SubspaceServer's <c>TeamVersusMatch.SendSpawnOverrides</c>. Configured per game type as
/// <c>GameType&lt;i&gt;Team&lt;t&gt;SpawnCenter</c> + <c>GameType&lt;i&gt;Team&lt;t&gt;SpawnRadius</c>.
/// </summary>
/// <remarks>
/// <para><see cref="Center"/> is in <b>pixels</b> (the same coordinate convention as the start
/// location, <see cref="StartPoint"/>), so an operator can reuse one coordinate for both. The
/// native client setting is in tiles, so <see cref="Center"/>'s <see cref="StartPoint.TileX"/> /
/// <see cref="StartPoint.TileY"/> are written to the client.</para>
/// <para><see cref="RadiusTiles"/> is in <b>tiles</b> (same unit as the start-drift budget and the
/// native 9-bit client radius field, which caps at 511). The client spawns the ship at a random
/// point within this radius of the center.</para>
/// <para>This is the in-match <em>respawn</em> location only; the <em>start</em> location (a
/// one-time server warp at match setup) is <see cref="StartPoint"/> / the queue's start-set.</para>
/// </remarks>
public readonly record struct SpawnArea(StartPoint Center, int RadiusTiles);
