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
    GroupId? Group = null);
