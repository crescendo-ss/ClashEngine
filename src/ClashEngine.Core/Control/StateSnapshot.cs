using System;
using System.Collections.Generic;

namespace ClashEngine.Core.Control;

/// <summary>
/// Full point-in-time matchmaking state: every queue with its waiters, every in-flight match,
/// and every active penalty timeout. A serialize-only DTO matching
/// <c>schema/snapshot.schema.json</c> 1:1 (keep in sync). This is the self-heal companion to the
/// outbound event stream: the stream is fire-and-forget and may drop under backpressure, so a
/// consumer rebuilds its world from a snapshot on (re)connect and applies events incrementally
/// from there. Built by <see cref="StateSnapshotBuilder"/>.
/// </summary>
public sealed record StateSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    IReadOnlyList<QueueStateSnapshot> Queues,
    IReadOnlyList<MatchStateSnapshot> Matches,
    IReadOnlyList<PenaltyStateSnapshot> Penalties);

/// <summary>One queue's registration + membership. <see cref="Members"/> is in queue (FIFO)
/// order. Present for every registered queue, including empty ones, so a consumer can render
/// the full queue board from a single snapshot. <see cref="TeamCount"/> ×
/// <see cref="PlayersPerTeam"/> is the match shape (= <see cref="Capacity"/>), telling a
/// consumer how to lay out <c>form_match</c> rosters and team-slot UI.</summary>
public sealed record QueueStateSnapshot(
    string QueueName,
    string QueueLabel,
    string? GameType,
    int Capacity,
    int TeamCount,
    int PlayersPerTeam,
    bool IgnorePenalties,
    IReadOnlyList<QueueMemberSnapshot> Members);

/// <summary>One waiting player. <see cref="Group"/> is an opaque tag shared by party members who
/// queued together (null for solo entries).</summary>
public sealed record QueueMemberSnapshot(
    string Player,
    DateTimeOffset EnqueuedAt,
    string? Group = null);

/// <summary>One in-flight (Forming or Live) match. <see cref="QueueName"/> is the queue the match
/// formed from -- for externally-formed matches, the anchor queue -- and is null only if that
/// queue was removed by a config reload while the match ran. <see cref="StartedAt"/> is null
/// while Forming.</summary>
public sealed record MatchStateSnapshot(
    Guid MatchId,
    string GameType,
    string? QueueName,
    string State,
    DateTimeOffset? StartedAt,
    IReadOnlyList<IReadOnlyList<string>> Teams);

/// <summary>One active queue-timeout: a (player, penalty-kind) pair whose timeout has not yet
/// expired at capture time.</summary>
public sealed record PenaltyStateSnapshot(
    string Player,
    string Kind,
    int OffenseCount,
    DateTimeOffset TimeoutUntil);
