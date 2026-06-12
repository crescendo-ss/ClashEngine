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
    /// <summary>
    /// A player has been added to a queue. <paramref name="initiator"/> is the party member who
    /// actually issued the enqueue command, when that's a different player from
    /// <paramref name="player"/> -- the engine sets this only for open-party group enqueues so the
    /// adapter can attribute the message ("X queued you for ..."). Null on solo enqueues, on the
    /// initiator's own row of a group enqueue, and on closed-party enqueues. KOTH re-enqueues
    /// fire <see cref="OnWinnerPromoted"/> instead of this event.
    /// </summary>
    void OnQueueAdded(PlayerKey player, string queueName, DateTimeOffset at, PlayerKey? initiator = null) { }

    /// <summary>
    /// A player left a queue. <paramref name="reason"/> distinguishes a user cancel from a
    /// disconnect, a match formation, an AFK cull, a party change, or an operator reset; it
    /// defaults to <see cref="QueueRemovalReason.Cancel"/> only as source-compatibility for
    /// external implementers -- every engine call site passes an explicit reason.
    /// </summary>
    void OnQueueRemoved(PlayerKey player, string queueName, DateTimeOffset at,
        QueueRemovalReason reason = QueueRemovalReason.Cancel) { }

    /// <summary>
    /// A player has been sitting in <paramref name="queueName"/> for <paramref name="dwell"/>,
    /// crossing the queue's AFK warning threshold for the first time this dwell-cycle. The engine
    /// will auto-dequeue them (firing <see cref="OnQueueRemoved"/> with
    /// <see cref="QueueRemovalReason.AfkCull"/>) once the cull threshold is reached, unless they
    /// re-queue first (which resets the timer). Adapters typically nudge the player to confirm
    /// they're still around.
    /// </summary>
    void OnQueueDwellWarning(PlayerKey player, string queueName, DateTimeOffset at, TimeSpan dwell) { }

    /// <summary>
    /// A player asked (via <c>?connect discord</c>) to link their in-game name to a Discord
    /// <paramref name="discordAlias"/>. Purely a relay: the engine neither stores nor validates
    /// the alias. The event-stream adapter forwards it to the external service, which performs the
    /// actual account link and opt-in; a chat adapter typically just acks the request.
    /// </summary>
    void OnDiscordLinkRequested(PlayerKey player, string discordAlias, DateTimeOffset at) { }

    /// <summary>
    /// A winning player was just auto-re-enqueued by KOTH mode
    /// (<c>QueueDefinition.PromoteWinnersToFront</c>) after a Completed match. Fires in place of
    /// <see cref="OnQueueAdded"/> so adapters can send a promotion-specific notice -- typically
    /// with a <c>?cancel</c> hint for players who don't want to be drawn into another match.
    /// </summary>
    /// <param name="defensesUsed">Number of consecutive defenses now credited to this player in
    /// this queue (after this win). 0 when <paramref name="sentToBack"/> is true (the engine
    /// resets the counter at the cap).</param>
    /// <param name="maxDefenses"><c>QueueDefinition.MaxConsecutiveDefenses</c> for the queue.</param>
    /// <param name="sentToBack">True when the winning team hit the defenses cap and was re-queued
    /// at the back rather than the head.</param>
    void OnWinnerPromoted(
        PlayerKey player, string queueName, DateTimeOffset at,
        int defensesUsed, int maxDefenses, bool sentToBack) { }

    /// <summary>
    /// A player with the <c>?autoqueue</c> preference enabled was automatically re-enqueued into
    /// <paramref name="queueName"/> -- the queue their just-finished match was formed from -- after
    /// a match that actually ran (Completed or Abandoned). Fires in place of
    /// <see cref="OnQueueAdded"/> so adapters can send an auto-queue-specific notice, typically with
    /// a <c>?cancel</c> / <c>?autoqueue off</c> hint for players who don't want to be drawn into
    /// another match. Not fired for KOTH winners (that's <see cref="OnWinnerPromoted"/>) nor when
    /// the player was already in the queue (e.g. a KOTH winner who also has auto-queue on).
    /// </summary>
    void OnAutoQueued(PlayerKey player, string queueName, DateTimeOffset at) { }

    /// <summary>
    /// A player's <c>?autoqueue</c> preference was automatically turned off because they were
    /// flagged for a staging AFK violation. Fires only when the preference was actually on (so the
    /// adapter only nudges players who lost something). The adapter notifies them they'll need to
    /// re-enable it with <c>?autoqueue on</c>.
    /// </summary>
    void OnAutoQueueDisabledByAfk(PlayerKey player, DateTimeOffset at) { }

    /// <summary>
    /// The queue just reached <c>TotalPlayers - 1</c> waiters (one short of forming a match) for
    /// the first time since it last dropped below that threshold. Gated to queues with
    /// <c>TotalPlayers &gt;= 4</c> -- the "near-full" framing is meaningless on tiny shapes (1v1,
    /// 2v2). <paramref name="waiting"/> is a snapshot of the queue's current waiter list at fire
    /// time, in queue order.
    /// </summary>
    void OnQueueNearFull(string queueName, IReadOnlyList<PlayerKey> waiting, int waitingCount, int needed) { }

    /// <summary>
    /// The matcher has found a viable partition for <paramref name="queueName"/> and is now
    /// holding it for up to <paramref name="holdWindow"/> in case additional arrivals improve the
    /// quality. Fired exactly once per hold-cycle (transition from no-held to held).
    /// </summary>
    void OnQueueHoldStarted(string queueName, IReadOnlyList<PlayerKey> candidates, double currentQuality, TimeSpan holdWindow) { }

    /// <summary>
    /// A held candidate was replaced with a higher-quality partition during the hold window. Fired
    /// only when the quality delta exceeds the matcher's improvement threshold so chat updates stay
    /// sparse.
    /// </summary>
    void OnQueueHoldImproved(string queueName, IReadOnlyList<PlayerKey> candidates, double oldQuality, double newQuality) { }

    /// <summary>
    /// A queue has enough waiters to form a match (<see cref="Matching.MatchShape.TotalPlayers"/> or
    /// more) but the matcher did not produce one this tick. <paramref name="status"/> says why
    /// (imbalance below threshold, no viable teams, or intentionally holding for better arrivals).
    /// Fired on <em>change</em> only -- when a queue first becomes blocked, when its reason changes,
    /// or when the best achievable quality shifts notably -- not every tick, so the Verbose log
    /// stays readable. The same status is cached and pulled live by <c>?queue</c>.
    /// </summary>
    void OnQueueMatchmakingBlocked(string queueName, QueueBlockStatus status, DateTimeOffset at) { }

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
    /// A match participant was just assessed an abandon (their per-player grace window expired
    /// mid-match) while <paramref name="survivors"/> -- their still-active teammates -- remain in
    /// the match. Since the abandoned teammate is no longer viable, those survivors may now leave
    /// without being assessed an abandon themselves; the adapter notifies them of that courtesy.
    /// Fires once per fresh abandonment transition and only while the match is still live.
    /// </summary>
    void OnTeammateAbandoned(IReadOnlyCollection<PlayerKey> survivors, PlayerKey abandoner, Guid matchId, DateTimeOffset at) { }

    /// <summary>
    /// A player was released from the match roster mid-match (e.g. lives-out elimination) but the
    /// match itself is still live. They stay in the match's stats record for end-of-match
    /// rating/upload purposes; this hook lets stats consumers close the player's open life and
    /// release any per-match index entry so the player can be matched into a new match.
    /// </summary>
    void OnPlayerReleasedFromMatch(PlayerKey player, Guid matchId, DateTimeOffset at) { }

    /// <summary>
    /// A participant who had departed mid-match (specced, left the arena, or disconnected) is
    /// Active in the match again. Fires only when the return actually took effect -- the player
    /// was in a returnable status and the match is still Forming/Live; no-op returns (knocked-out
    /// players, stale ship-change callbacks) don't fire. Stats consumers use this to re-arm
    /// per-connection wiring that does not survive the departure (e.g. the damage-callback watch:
    /// the client-side damage-reporting toggle dies on arena re-entry and reconnect).
    /// </summary>
    void OnPlayerReturnedToMatch(PlayerKey player, Guid matchId, DateTimeOffset at) { }

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

    /// <summary>
    /// A team has had no Active member inside the game type's presence zone since
    /// <paramref name="since"/> (the team's last confirmed presence) and will forfeit at
    /// <paramref name="forfeitAt"/> unless someone re-enters the zone first. The adapter
    /// typically broadcasts a "return to the zone or forfeit" warning.
    /// </summary>
    void OnZoneVacated(ActiveMatch match, int teamIdx, DateTimeOffset since, DateTimeOffset forfeitAt) { }

    /// <summary>
    /// A team that was previously flagged zone-vacant got a player back inside the presence zone
    /// before the timeout. Pairs with a prior <see cref="OnZoneVacated"/> for the same team.
    /// </summary>
    void OnZoneReclaimed(ActiveMatch match, int teamIdx) { }

    /// <summary>A heuristic flagged a player as a griefer; the penalty is now active and the veto
    /// window is open. <paramref name="timeoutUntil"/> is when the player's queue-lock currently
    /// ends if the penalty stands (already includes any in-effect Abandonment timeout).</summary>
    void OnGriefingFlagged(PendingGriefingPenalty pending, DateTimeOffset timeoutUntil) { }

    /// <summary>A match participant voted to veto a pending griefing penalty; threshold not yet met.</summary>
    void OnVetoRecorded(PendingGriefingPenalty pending, PlayerKey voter) { }

    /// <summary>Enough match participants vetoed: the griefing penalty has been rescinded.</summary>
    void OnGriefingVetoed(PendingGriefingPenalty pending) { }

    /// <summary>The griefing penalty is now final -- either the veto window expired without
    /// enough vetoes, or the penalty was applied without ever opening a veto window because the
    /// match had too few eligible voters. <paramref name="timeoutUntil"/> is when the player's
    /// queue-lock currently ends (already includes any in-effect Abandonment timeout).</summary>
    void OnGriefingConfirmed(PendingGriefingPenalty pending, DateTimeOffset timeoutUntil) { }

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
