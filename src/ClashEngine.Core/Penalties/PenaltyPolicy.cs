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

    /// <summary>
    /// Default for staging-phase AFK: 1 minute first offense, doubles each repeat. Milder than
    /// <see cref="DefaultAbandonment"/> because the match was cancelled before it ever started --
    /// nobody else lost the match because of the AFK -- but escalates because chronic
    /// no-readies still waste everyone else's time.
    /// </summary>
    public static PenaltyPolicy DefaultStagingAfk { get; } =
        new(PenaltyKind.StagingAfk, TimeSpan.FromMinutes(1), 2.0, TimeSpan.FromHours(24));

    /// <summary>
    /// Hard ceiling on a single computed timeout. Anything beyond this is effectively a
    /// permanent ban for the session, but the bounded value keeps downstream
    /// <c>DateTimeOffset</c> arithmetic from overflowing in <see cref="PenaltyTracker"/>.
    /// Picked at ~10 years so it's "forever" for any actual queue use but well clear of
    /// <see cref="DateTimeOffset.MaxValue"/>.
    /// </summary>
    private static readonly TimeSpan MaxComputableTimeout = TimeSpan.FromDays(365 * 10);

    public TimeSpan TimeoutForOffense(int offenseCount)
    {
        if (offenseCount < 1) throw new ArgumentOutOfRangeException(nameof(offenseCount));
        double seconds = BaseTimeout.TotalSeconds * Math.Pow(EscalationFactor, offenseCount - 1);
        // BaseTimeout * EscalationFactor^N grows unbounded; with the default 10-minute / 2x
        // ladder, the 42nd offense already exceeds TimeSpan.FromSeconds's range. Clamp instead
        // of throwing so a player with a long penalty history can still have ?play / status
        // queries answered. Also covers NaN (bad config) by treating it as "max".
        if (double.IsNaN(seconds) || seconds >= MaxComputableTimeout.TotalSeconds)
            return MaxComputableTimeout;
        return TimeSpan.FromSeconds(seconds);
    }
}
