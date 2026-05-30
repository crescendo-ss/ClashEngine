using System;
using System.Collections.Generic;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;

namespace ClashEngine.Adapter;

/// <summary>Fans an <see cref="IMatchmakingTelemetry"/> event out to multiple listeners.</summary>
public sealed class CompositeTelemetry : IMatchmakingTelemetry
{
    private readonly IReadOnlyList<IMatchmakingTelemetry> _listeners;

    public CompositeTelemetry(params IMatchmakingTelemetry[] listeners)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        _listeners = listeners;
    }

    public void OnQueueAdded(PlayerKey p, string q, DateTimeOffset at, PlayerKey? initiator = null)
    {
        foreach (var l in _listeners) l.OnQueueAdded(p, q, at, initiator);
    }
    public void OnQueueRemoved(PlayerKey p, string q, DateTimeOffset at,
        QueueRemovalReason reason = QueueRemovalReason.Cancel)
    {
        foreach (var l in _listeners) l.OnQueueRemoved(p, q, at, reason);
    }
    public void OnQueueDwellWarning(PlayerKey p, string q, DateTimeOffset at, TimeSpan dwell)
    {
        foreach (var l in _listeners) l.OnQueueDwellWarning(p, q, at, dwell);
    }
    public void OnDiscordLinkRequested(PlayerKey p, string discordAlias, DateTimeOffset at)
    {
        foreach (var l in _listeners) l.OnDiscordLinkRequested(p, discordAlias, at);
    }
    public void OnWinnerPromoted(PlayerKey p, string q, DateTimeOffset at, int defensesUsed, int maxDefenses, bool sentToBack)
    {
        foreach (var l in _listeners) l.OnWinnerPromoted(p, q, at, defensesUsed, maxDefenses, sentToBack);
    }
    public void OnAutoQueued(PlayerKey p, string q, DateTimeOffset at)
    {
        foreach (var l in _listeners) l.OnAutoQueued(p, q, at);
    }
    public void OnAutoQueueDisabledByAfk(PlayerKey p, DateTimeOffset at)
    {
        foreach (var l in _listeners) l.OnAutoQueueDisabledByAfk(p, at);
    }
    public void OnQueueNearFull(string q, IReadOnlyList<PlayerKey> waiting, int waitingCount, int needed)
    {
        foreach (var l in _listeners) l.OnQueueNearFull(q, waiting, waitingCount, needed);
    }
    public void OnQueueHoldStarted(string q, IReadOnlyList<PlayerKey> candidates, double quality, TimeSpan holdWindow)
    {
        foreach (var l in _listeners) l.OnQueueHoldStarted(q, candidates, quality, holdWindow);
    }
    public void OnQueueHoldImproved(string q, IReadOnlyList<PlayerKey> candidates, double oldQ, double newQ)
    {
        foreach (var l in _listeners) l.OnQueueHoldImproved(q, candidates, oldQ, newQ);
    }
    public void OnMatchProposed(MatchProposal proposal)
    {
        foreach (var l in _listeners) l.OnMatchProposed(proposal);
    }
    public void OnMatchStarted(ActiveMatch match)
    {
        foreach (var l in _listeners) l.OnMatchStarted(match);
    }
    public void OnMatchEnded(MatchOutcome outcome)
    {
        foreach (var l in _listeners) l.OnMatchEnded(outcome);
    }
    public void OnAbandonment(PlayerKey p, int n, DateTimeOffset until)
    {
        foreach (var l in _listeners) l.OnAbandonment(p, n, until);
    }
    public void OnTeammateAbandoned(IReadOnlyCollection<PlayerKey> survivors, PlayerKey abandoner, Guid matchId, DateTimeOffset at)
    {
        foreach (var l in _listeners) l.OnTeammateAbandoned(survivors, abandoner, matchId, at);
    }
    public void OnPlayerReleasedFromMatch(PlayerKey p, Guid matchId, DateTimeOffset at)
    {
        foreach (var l in _listeners) l.OnPlayerReleasedFromMatch(p, matchId, at);
    }
    public void OnTeamCollapsing(ActiveMatch m, int teamIdx, DateTimeOffset since, DateTimeOffset forfeitAt)
    {
        foreach (var l in _listeners) l.OnTeamCollapsing(m, teamIdx, since, forfeitAt);
    }
    public void OnTeamRecovered(ActiveMatch m, int teamIdx)
    {
        foreach (var l in _listeners) l.OnTeamRecovered(m, teamIdx);
    }
    public void OnGriefingFlagged(PendingGriefingPenalty pending, DateTimeOffset timeoutUntil)
    {
        foreach (var l in _listeners) l.OnGriefingFlagged(pending, timeoutUntil);
    }
    public void OnVetoRecorded(PendingGriefingPenalty pending, PlayerKey voter)
    {
        foreach (var l in _listeners) l.OnVetoRecorded(pending, voter);
    }
    public void OnGriefingVetoed(PendingGriefingPenalty pending)
    {
        foreach (var l in _listeners) l.OnGriefingVetoed(pending);
    }
    public void OnGriefingConfirmed(PendingGriefingPenalty pending, DateTimeOffset timeoutUntil)
    {
        foreach (var l in _listeners) l.OnGriefingConfirmed(pending, timeoutUntil);
    }
    public void OnGroupDisbanded(IReadOnlyCollection<PlayerKey> notify, PlayerKey trigger, DisbandReason reason)
    {
        foreach (var l in _listeners) l.OnGroupDisbanded(notify, trigger, reason);
    }
    public void OnInviteSent(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at, TimeSpan ttl)
    {
        foreach (var l in _listeners) l.OnInviteSent(inviter, invitee, at, ttl);
    }
    public void OnInviteAccepted(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        foreach (var l in _listeners) l.OnInviteAccepted(inviter, invitee, at);
    }
    public void OnInviteDeclined(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        foreach (var l in _listeners) l.OnInviteDeclined(inviter, invitee, at);
    }
    public void OnInviteExpired(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        foreach (var l in _listeners) l.OnInviteExpired(inviter, invitee, at);
    }
}
