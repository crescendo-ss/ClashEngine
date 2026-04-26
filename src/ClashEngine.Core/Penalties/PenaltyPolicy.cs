using System;

namespace ClashEngine.Core.Penalties;

/// <summary>
/// Per-kind escalation policy. Timeout for the Nth offense (1-indexed) is
/// <c>BaseTimeout * EscalationFactor^(N-1)</c>. Offenses count as part of the same ladder
/// only if consecutive offenses are within <see cref="MemoryWindow"/> of each other.
/// </summary>
public sealed class PenaltyPolicy
{
    public PenaltyPolicy(PenaltyKind kind, TimeSpan baseTimeout, double escalationFactor, TimeSpan memoryWindow)
    {
        if (baseTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseTimeout), "Must be non-negative.");
        if (escalationFactor < 1.0)
            throw new ArgumentOutOfRangeException(nameof(escalationFactor), "Must be >= 1.");
        if (memoryWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(memoryWindow), "Must be non-negative.");

        Kind = kind;
        BaseTimeout = baseTimeout;
        EscalationFactor = escalationFactor;
        MemoryWindow = memoryWindow;
    }

    public PenaltyKind Kind { get; }
    public TimeSpan BaseTimeout { get; }
    public double EscalationFactor { get; }
    public TimeSpan MemoryWindow { get; }

    public static PenaltyPolicy DefaultAbandonment { get; } =
        new(PenaltyKind.Abandonment, TimeSpan.FromMinutes(10), 2.0, TimeSpan.FromHours(24));

    public static PenaltyPolicy DefaultGriefing { get; } =
        new(PenaltyKind.Griefing, TimeSpan.FromMinutes(5), 2.0, TimeSpan.FromHours(24));

    /// <summary>
    /// Default for elimination cooldown: 1 minute, no escalation, short memory window so
    /// repeated eliminations within the same play session don't compound.
    /// </summary>
    public static PenaltyPolicy DefaultEliminationCooldown { get; } =
        new(PenaltyKind.EliminationCooldown, TimeSpan.FromMinutes(1), 1.0, TimeSpan.FromMinutes(5));

    public TimeSpan TimeoutForOffense(int offenseCount)
    {
        if (offenseCount < 1) throw new ArgumentOutOfRangeException(nameof(offenseCount));
        double seconds = BaseTimeout.TotalSeconds * Math.Pow(EscalationFactor, offenseCount - 1);
        return TimeSpan.FromSeconds(seconds);
    }
}
