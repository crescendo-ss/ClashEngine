namespace ClashEngine.Core.Queue;

/// <summary>
/// Spawn for one team in a match, in <b>pixel</b> coordinates (1 tile = 16 px), matching the
/// documented <c>GameType&lt;i&gt;Team&lt;t&gt;Spawns</c> config contract (see README / schema).
/// The orchestrator warps players here; <see cref="TileX"/>/<see cref="TileY"/> give the
/// tile-coordinate form that SubspaceServer's <c>IGame.WarpTo</c> / <c>SendToArena</c> expect.
/// </summary>
public readonly record struct SpawnPoint(short X, short Y)
{
    /// <summary>X in tile coordinates (pixels / 16) for the SS warp APIs.</summary>
    public short TileX => (short)(X >> 4);

    /// <summary>Y in tile coordinates (pixels / 16) for the SS warp APIs.</summary>
    public short TileY => (short)(Y >> 4);
}
