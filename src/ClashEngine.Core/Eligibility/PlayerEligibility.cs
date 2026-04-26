using System;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Eligibility;

public enum EligibilityStatus
{
    /// <summary>Player can join a queue.</summary>
    Available,

    /// <summary>Player is currently participating in a match (Forming, Live, or in grace).</summary>
    InMatch,

    /// <summary>Player is serving a queue-timeout (abandonment, griefing, or any other penalty).</summary>
    InTimeout,

    /// <summary>Player is not connected to the zone.</summary>
    Disconnected,
}

/// <summary>
/// Result of an eligibility check. <see cref="TimeoutUntil"/> is set only for
/// <see cref="EligibilityStatus.InTimeout"/>.
/// </summary>
public readonly record struct EligibilityResult(EligibilityStatus Status, DateTimeOffset? TimeoutUntil = null);

/// <summary>
/// Pure query that decides whether a player may currently queue. Priority
/// (highest first): <c>Disconnected -> InMatch -> InTimeout -> Available</c>.
/// </summary>
public sealed class PlayerEligibility
{
    private readonly PenaltyTracker _penalties;
    private readonly IClock _clock;

    public PlayerEligibility(PenaltyTracker penalties, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(penalties);
        ArgumentNullException.ThrowIfNull(clock);
        _penalties = penalties;
        _clock = clock;
    }

    public EligibilityResult Check(PlayerKey player, bool isConnected, bool isInActiveMatch)
    {
        if (!isConnected)
            return new EligibilityResult(EligibilityStatus.Disconnected);

        if (isInActiveMatch)
            return new EligibilityResult(EligibilityStatus.InMatch);

        var now = _clock.UtcNow;
        if (_penalties.IsInTimeout(player, now))
            return new EligibilityResult(EligibilityStatus.InTimeout, _penalties.TimeoutUntil(player));

        return new EligibilityResult(EligibilityStatus.Available);
    }
}
