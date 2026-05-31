using System;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core.Queue;

/// <summary>
/// A single entry sitting in a <see cref="PlayerQueue"/>. Captures the rating snapshot
/// at enqueue time so subsequent rating updates don't perturb in-flight matchmaking.
/// <see cref="Group"/> is set when the player queued as part of a group; the matcher uses it
/// to prefer keeping group members on the same team.
/// </summary>
public readonly record struct QueueEntry(
    PlayerKey Player,
    Rating Rating,
    DateTimeOffset EnqueuedAt,
    GroupId? Group = null)
{
    /// <summary>
    /// Last time the player re-affirmed their presence by re-issuing <c>?play</c> while already
    /// queued (a liveness ping). This -- not <see cref="EnqueuedAt"/> -- drives the AFK dwell
    /// warn/cull clock, so a present player can refresh it to avoid being culled. Defaults to
    /// <see cref="EnqueuedAt"/>. <see cref="EnqueuedAt"/> is deliberately left untouched on a
    /// refresh so queue ordering, the wait-based match-quality relaxation, and the displayed
    /// time-in-queue all keep reflecting true time since the player first joined.
    /// </summary>
    public DateTimeOffset LastSeenAt { get; init; } = EnqueuedAt;
}
