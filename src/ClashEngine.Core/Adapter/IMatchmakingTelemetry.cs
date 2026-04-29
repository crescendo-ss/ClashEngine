using System;
using System.Collections.Generic;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Core.Adapter;

/// <summary>
/// Outbound observability hooks. The adapter provides an implementation that pipes events to
/// <c>ILogManager</c> / structured logs / metrics. The pure layer never blocks on this. Every
/// method has an empty default body so implementers only override the events they care about.
/// </summary>
public interface IMatchmakingTelemetry
{
    void OnQueueAdded(PlayerKey player, string queueName, DateTimeOffset at) { }
    void OnQueueRemoved(PlayerKey player, string queueName, DateTimeOffset at) { }

    /// <summary>A new group invitation was just accepted by the registry. Fired only on the
    /// successful (<see cref="Groups.InviteResult.Sent"/>) path; the adapter typically translates
    /// this into a DM to <paramref name="invitee"/> so they know to <c>?accept</c>.</summary>
    void OnInviteSent(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at, TimeSpan ttl) { }

    /// <summary>The invitee accepted a pending invitation. The adapter typically DMs the inviter
    /// so they know the recipient joined.</summary>
    void OnInviteAccepted(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at) { }

    /// <summary>The invitee explicitly declined a pending invitation. The adapter typically DMs
    /// the inviter so they know the recipient said no.</summary>
    void OnInviteDeclined(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at) { }

    /// <summary>A previously-sent invitation reached its TTL with no accept/decline. The adapter
    /// typically DMs the inviter so they know it lapsed unanswered.</summary>
    void OnInviteExpired(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at) { }
    void OnMatchProposed(MatchProposal proposal) { }
    void OnMatchStarted(ActiveMatch match) { }
    void OnMatchEnded(MatchOutcome outcome) { }
    void OnAbandonment(PlayerKey player, int offenseCount, DateTimeOffset timeoutUntil) { }

    /// <summary>
    /// A team just lost its last live member; the team-collapse grace timer started at
    /// <paramref name="since"/> and the team will forfeit at <paramref name="forfeitAt"/> unless
    /// at least one player returns to active before then.
    /// </summary>
    void OnTeamCollapsing(ActiveMatch match, int teamIdx, DateTimeOffset since, DateTimeOffset forfeitAt) { }

    /// <summary>
    /// A team that was previously collapsing got a player back before the grace expired. Pairs
    /// with a prior <see cref="OnTeamCollapsing"/> for the same team.
    /// </summary>
    void OnTeamRecovered(ActiveMatch match, int teamIdx) { }

    /// <summary>A heuristic flagged a player as a griefer; the penalty is now active and the veto
    /// window is open.</summary>
    void OnGriefingFlagged(PendingGriefingPenalty pending) { }

    /// <summary>A match participant voted to veto a pending griefing penalty; threshold not yet met.</summary>
    void OnVetoRecorded(PendingGriefingPenalty pending, PlayerKey voter) { }

    /// <summary>Enough match participants vetoed: the griefing penalty has been rescinded.</summary>
    void OnGriefingVetoed(PendingGriefingPenalty pending) { }

    /// <summary>The veto window expired without enough vetoes; the penalty is now confirmed.</summary>
    void OnGriefingConfirmed(PendingGriefingPenalty pending) { }

    /// <summary>
    /// A group was just dissolved. <paramref name="notify"/> is the set of remaining members the
    /// adapter should chat-notify (excludes whoever triggered the disband -- their own command
    /// handler / auto-drop path messages them). <paramref name="trigger"/> is the player whose
    /// leave caused the disband; <paramref name="reason"/> tells the adapter how to phrase the
    /// notification.
    /// </summary>
    void OnGroupDisbanded(IReadOnlyCollection<PlayerKey> notify, PlayerKey trigger, DisbandReason reason) { }
}

/// <summary>Default no-op telemetry sink. With default-interface methods on
/// <see cref="IMatchmakingTelemetry"/> this is just a concrete handle for the engine's "no
/// telemetry" baseline; useful where a non-null sink is required (e.g. constructor defaults).</summary>
public sealed class NoOpTelemetry : IMatchmakingTelemetry
{
    public static NoOpTelemetry Instance { get; } = new();
}
