using System;

namespace ClashEngine.Core.Penalties;

/// <summary>
/// Per-kind escalation policy. Timeout for the Nth offense (1-indexed) is
/// <c>BaseTimeout * EscalationFactor^(N-1)</c>. Offenses count as part of the same ladder
/// only if consecutive offenses are within <see cref="MemoryWindow"/> of each other.
/// </summary>
public sealed class PenaltyPolicy
{
    /// <summary>
    /// Default ceiling on the assessed timeout (after escalation and severity). 6 hours is long
    /// enough to be a real consequence for repeat offenders but bounded so a buggy or runaway
    /// ladder can't lock a player out for years -- see the persistence-doubling incident that
    /// surfaced an effective ~8898h penalty before this cap existed.
    /// </summary>
    public static readonly TimeSpan DefaultMaxTimeout = TimeSpan.FromHours(6);

    public PenaltyPolicy(
        PenaltyKind kind,
        TimeSpan baseTimeout,
        double escalationFactor,
        TimeSpan memoryWindow,
        TimeSpan? maxTimeout = null)
    {
        if (baseTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseTimeout), "Must be non-negative.");
        if (escalationFactor < 1.0)
            throw new ArgumentOutOfRangeException(nameof(escalationFactor), "Must be >= 1.");
        if (memoryWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(memoryWindow), "Must be non-negative.");
        if (maxTimeout is { } mt && mt <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxTimeout), "Must be positive when specified.");

        Kind = kind;
        BaseTimeout = baseTimeout;
        EscalationFactor = escalationFactor;
        MemoryWindow = memoryWindow;
        MaxTimeout = maxTimeout ?? DefaultMaxTimeout;
    }

    public PenaltyKind Kind { get; }
    public TimeSpan BaseTimeout { get; }
    public double EscalationFactor { get; }
    public TimeSpan MemoryWindow { get; }

    /// <summary>
    /// Hard ceiling on the assessed timeout (i.e. the value
    /// <see cref="EffectiveTimeoutFor"/> can return). Applied after escalation and severity.
    /// Defaults to <see cref="DefaultMaxTimeout"/>.
    /// </summary>
    public TimeSpan MaxTimeout { get; }

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

    /// <summary>Returns a copy of this policy with <see cref="MaxTimeout"/> overridden.</summary>
    public PenaltyPolicy WithMaxTimeout(TimeSpan maxTimeout) =>
        new(Kind, BaseTimeout, EscalationFactor, MemoryWindow, maxTimeout);

    /// <summary>
    /// Hard ceiling used inside <see cref="TimeoutForOffense"/> to keep the unscaled ladder from
    /// overflowing <see cref="TimeSpan.FromSeconds(double)"/> when <see cref="EscalationFactor"/>
    /// is &gt; 1 and the offense count is large. The user-facing cap is <see cref="MaxTimeout"/>
    /// applied in <see cref="EffectiveTimeoutFor"/>; this one is just an arithmetic guard.
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

    /// <summary>
    /// Final assessed timeout: <see cref="TimeoutForOffense"/> times <paramref name="severity"/>,
    /// clamped to <see cref="MaxTimeout"/>. This is what <see cref="PenaltyTracker"/> uses to
    /// compute the wall-clock end of a player's penalty.
    /// </summary>
    public TimeSpan EffectiveTimeoutFor(int offenseCount, double severity = 1.0)
    {
        if (severity < 1.0) throw new ArgumentOutOfRangeException(nameof(severity), "Must be >= 1.");
        var baseTimeout = TimeoutForOffense(offenseCount);
        double seconds = baseTimeout.TotalSeconds * severity;
        if (double.IsNaN(seconds) || seconds >= MaxTimeout.TotalSeconds)
            return MaxTimeout;
        return TimeSpan.FromSeconds(seconds);
    }
}
