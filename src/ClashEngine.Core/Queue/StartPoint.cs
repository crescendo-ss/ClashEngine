namespace ClashEngine.Core.Queue;

/// <summary>
/// One team's <b>match starting location</b>, in <b>tile</b> coordinates (1 tile = 16 px),
/// matching the documented <c>GameType&lt;i&gt;Team&lt;t&gt;Starts</c> config contract (see README
/// / schema). At setup the orchestrator server-side warps every player on the team here (a
/// one-time teleport); <see cref="TileX"/>/<see cref="TileY"/> are the tile form SubspaceServer's
/// <c>IGame.WarpTo</c> / <c>SendToArena</c> expect, and <see cref="PixelX"/>/<see cref="PixelY"/>
/// are the position-packet (pixel) form for drift comparisons. This is distinct from the per-team
/// <em>respawn</em> box (<see cref="SpawnArea"/>), which controls where the client respawns the
/// player after a death via client-settings overrides.
/// </summary>
public readonly record struct StartPoint(short X, short Y)
{
    /// <summary>X in tile coordinates for the SS warp APIs (this is the stored value).</summary>
    public short TileX => X;

    /// <summary>Y in tile coordinates for the SS warp APIs (this is the stored value).</summary>
    public short TileY => Y;

    /// <summary>X in pixel coordinates (tiles * 16) for position-packet comparisons.</summary>
    public int PixelX => X << 4;

    /// <summary>Y in pixel coordinates (tiles * 16) for position-packet comparisons.</summary>
    public int PixelY => Y << 4;
}
