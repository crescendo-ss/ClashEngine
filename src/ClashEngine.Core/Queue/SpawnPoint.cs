namespace ClashEngine.Core.Queue;

/// <summary>Tile-coordinate spawn for one team in a match. Used by the orchestrator to warp.</summary>
public readonly record struct SpawnPoint(short X, short Y);
