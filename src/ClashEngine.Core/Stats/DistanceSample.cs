namespace ClashEngine.Core.Stats;

/// <summary>
/// One periodic snapshot of a player's distance to the nearest live enemy. Recorded by the
/// host's distance sampler on a fixed cadence (default 5 Hz). Distance is in pixels --
/// SubspaceServer's native position unit, where 16 pixels equal one map tile.
/// </summary>
/// <remarks>
/// <para>"Live enemy" means a participant on an opposing team in the same match who is in-arena
/// with lives remaining (or unlimited lives). If no live enemy can be located at sample time --
/// e.g. every opponent has been eliminated, or all are out of arena -- the sampler skips that
/// tick rather than emitting a sentinel. Samples are appended in chronological order.</para>
///
/// <para>Stored as a value type so a list of these is dense in memory regardless of match
/// duration; serialized as <c>{ "tick": ..., "distance": ... }</c>.</para>
/// </remarks>
public readonly record struct DistanceSample(uint Tick, float Distance);
