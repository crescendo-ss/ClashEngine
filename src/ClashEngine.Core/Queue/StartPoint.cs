namespace ClashEngine.Core.Queue;

/// <summary>
/// One team's <b>match starting location</b>, in <b>pixel</b> coordinates (1 tile = 16 px),
/// matching the documented <c>GameType&lt;i&gt;Team&lt;t&gt;Starts</c> config contract (see README
/// / schema). At setup the orchestrator server-side warps every player on the team here (a
/// one-time teleport); <see cref="TileX"/>/<see cref="TileY"/> give the tile-coordinate form that
/// SubspaceServer's <c>IGame.WarpTo</c> / <c>SendToArena</c> expect. This is distinct from the
/// per-team <em>respawn</em> box (<see cref="SpawnArea"/>), which controls where the client
/// respawns the player after a death via client-settings overrides.
/// </summary>
public readonly record struct StartPoint(short X, short Y)
{
    /// <summary>X in tile coordinates (pixels / 16) for the SS warp APIs.</summary>
    public short TileX => (short)(X >> 4);

    /// <summary>Y in tile coordinates (pixels / 16) for the SS warp APIs.</summary>
    public short TileY => (short)(Y >> 4);
}
