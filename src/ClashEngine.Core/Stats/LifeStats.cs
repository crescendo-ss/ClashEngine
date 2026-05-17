using ClashEngine.Core.Identity;

namespace ClashEngine.Core.Stats;

/// <summary>
/// Stats accumulated within a single life: from spawn until the life ends (kill, leaving the
/// match, or match completion). Aggregated into <see cref="PlayerStats"/>'s lives list so that
/// per-life trends can be reported alongside match totals.
/// </summary>
public sealed class LifeStats
{
    public LifeStats(uint startTick)
    {
        StartTick = startTick;
    }

    public uint StartTick { get; }
    public uint? EndTick { get; private set; }
    public LifeEndReason? EndReason { get; private set; }

    /// <summary>The player whose damage closed this life. Null when the life ended without a kill.</summary>
    public PlayerKey? KnockoutBy { get; private set; }

    /// <summary>Canonical ship name (<c>"Warbird"</c>..<c>"Shark"</c>) the player was in when this
    /// life ended -- at death, or at match-end before being specced. Null when the ship was never
    /// observed for this player (e.g. the life closed before any ship-change was seen).</summary>
    public string? ShipAtEnd { get; private set; }

    public int Kills { get; private set; }
    public int DamageDealt { get; private set; }
    public int DamageTaken { get; private set; }

    public bool IsClosed => EndTick.HasValue;

    internal void Close(uint endTick, LifeEndReason reason, PlayerKey? knockoutBy, string? shipAtEnd)
    {
        EndTick = endTick;
        EndReason = reason;
        KnockoutBy = knockoutBy;
        ShipAtEnd = shipAtEnd;
    }

    internal void RecordKill() => Kills++;
    internal void AddDamageDealt(int amount) => DamageDealt += amount;
    internal void AddDamageTaken(int amount) => DamageTaken += amount;
}
