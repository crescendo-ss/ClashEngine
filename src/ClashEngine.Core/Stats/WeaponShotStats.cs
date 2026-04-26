namespace ClashEngine.Core.Stats;

/// <summary>
/// Per-weapon shot accounting: number of shots fired, number that produced an opponent-damage
/// event (for accuracy / hit-rate), and total energy spent across those shots.
/// </summary>
/// <remarks>
/// <para>For bombs and mines a single shot may produce multiple hit events when its splash
/// damages multiple enemies; this is <em>intentional</em> and is why displays of this metric
/// are labelled "hit rate" rather than "accuracy" -- the value can exceed 100%.</para>
/// </remarks>
public sealed class WeaponShotStats
{
    public int FireCount { get; private set; }
    public int HitCount { get; private set; }
    public long EnergySpent { get; private set; }

    internal void RecordShot(long energy)
    {
        FireCount++;
        EnergySpent += energy;
    }

    internal void RecordHit() => HitCount++;
}
