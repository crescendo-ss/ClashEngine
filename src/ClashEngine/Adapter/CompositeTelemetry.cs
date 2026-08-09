using System;
using System.Collections.Generic;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Adapter;

/// <summary>Fans an <see cref="IMatchmakingTelemetry"/> event out to multiple listeners.</summary>
/// <remarks>
/// Every fan-out is fault-isolated per listener: a listener that throws is logged and skipped,
/// and the remaining listeners still receive the event. Registration order is load-bearing (see
/// <c>ClashModule.PostLoadAsync</c>) but ordering is not the same as coupling -- without this
/// isolation a single failing listener silently truncated the chain. One real incident:
/// <c>ClashStatsTelemetry.OnMatchEnded</c> never ran because an earlier listener threw, so the
/// stats registry kept its player-&gt;match index for a finished match and every participant's
/// next match start threw "already in match", which in turn aborted <c>OnMatchStarted</c> before
/// the LVZ adapter and freq advisor were initialized -- taking down a match for everyone in it.
/// </remarks>
public sealed class CompositeTelemetry : IMatchmakingTelemetry
{
    private const string LogCategory = nameof(CompositeTelemetry);

    private readonly IReadOnlyList<IMatchmakingTelemetry> _listeners;
    private readonly ILogManager? _log;

    /// <param name="log">Sink for per-listener failures; <see langword="null"/> drops them
    /// silently (isolation still applies).</param>
    public CompositeTelemetry(ILogManager? log, params IMatchmakingTelemetry[] listeners)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        _listeners = listeners;
        _log = log;
    }

    /// <summary>
    /// Invokes <paramref name="call"/> on every listener, containing any exception to the
    /// listener that raised it. <paramref name="eventName"/> only labels the log line.
    /// </summary>
    private void Fan(Action<IMatchmakingTelemetry> call, string eventName)
    {
        for (int i = 0; i < _listeners.Count; i++)
        {
            var listener = _listeners[i];
            try
            {
                call(listener);
            }
            catch (Exception ex)
            {
                _log?.LogM(LogLevel.Error, LogCategory,
                    $"{listener.GetType().Name}.{eventName} threw; continuing with the remaining listeners: {ex}");
            }
        }
    }

    public void OnQueueAdded(PlayerKey p, string q, DateTimeOffset at, PlayerKey? initiator = null)
    {
        Fan(l => l.OnQueueAdded(p, q, at, initiator), nameof(OnQueueAdded));
    }
    public void OnQueueRemoved(PlayerKey p, string q, DateTimeOffset at,
        QueueRemovalReason reason = QueueRemovalReason.Cancel)
    {
        Fan(l => l.OnQueueRemoved(p, q, at, reason), nameof(OnQueueRemoved));
    }
    public void OnQueueDwellWarning(PlayerKey p, string q, DateTimeOffset at, TimeSpan dwell)
    {
        Fan(l => l.OnQueueDwellWarning(p, q, at, dwell), nameof(OnQueueDwellWarning));
    }
    public void OnDiscordLinkRequested(PlayerKey p, string discordAlias, DateTimeOffset at)
    {
        Fan(l => l.OnDiscordLinkRequested(p, discordAlias, at), nameof(OnDiscordLinkRequested));
    }
    public void OnWinnerPromoted(PlayerKey p, string q, DateTimeOffset at, int defensesUsed, int maxDefenses, bool sentToBack)
    {
        Fan(l => l.OnWinnerPromoted(p, q, at, defensesUsed, maxDefenses, sentToBack), nameof(OnWinnerPromoted));
    }
    public void OnAutoQueued(PlayerKey p, string q, DateTimeOffset at)
    {
        Fan(l => l.OnAutoQueued(p, q, at), nameof(OnAutoQueued));
    }
    public void OnQueueRestored(PlayerKey p, string q, DateTimeOffset at)
    {
        Fan(l => l.OnQueueRestored(p, q, at), nameof(OnQueueRestored));
    }
    public void OnAutoQueueDisabledByAfk(PlayerKey p, DateTimeOffset at)
    {
        Fan(l => l.OnAutoQueueDisabledByAfk(p, at), nameof(OnAutoQueueDisabledByAfk));
    }
    public void OnQueueNearFull(string q, IReadOnlyList<PlayerKey> waiting, int waitingCount, int needed)
    {
        Fan(l => l.OnQueueNearFull(q, waiting, waitingCount, needed), nameof(OnQueueNearFull));
    }
    public void OnQueueMatchmakingBlocked(string q, QueueBlockStatus status, DateTimeOffset at)
    {
        Fan(l => l.OnQueueMatchmakingBlocked(q, status, at), nameof(OnQueueMatchmakingBlocked));
    }
    public void OnQueueHoldStarted(string q, IReadOnlyList<PlayerKey> candidates, double quality, TimeSpan holdWindow)
    {
        Fan(l => l.OnQueueHoldStarted(q, candidates, quality, holdWindow), nameof(OnQueueHoldStarted));
    }
    public void OnQueueHoldImproved(string q, IReadOnlyList<PlayerKey> candidates, double oldQ, double newQ)
    {
        Fan(l => l.OnQueueHoldImproved(q, candidates, oldQ, newQ), nameof(OnQueueHoldImproved));
    }
    public void OnMatchProposed(MatchProposal proposal)
    {
        Fan(l => l.OnMatchProposed(proposal), nameof(OnMatchProposed));
    }
    public void OnMatchStarted(ActiveMatch match)
    {
        Fan(l => l.OnMatchStarted(match), nameof(OnMatchStarted));
    }
    public void OnMatchEnded(MatchOutcome outcome)
    {
        Fan(l => l.OnMatchEnded(outcome), nameof(OnMatchEnded));
    }
    public void OnAbandonment(PlayerKey p, int n, DateTimeOffset until)
    {
        Fan(l => l.OnAbandonment(p, n, until), nameof(OnAbandonment));
    }
    public void OnTeammateAbandoned(IReadOnlyCollection<PlayerKey> survivors, PlayerKey abandoner, Guid matchId, DateTimeOffset at)
    {
        Fan(l => l.OnTeammateAbandoned(survivors, abandoner, matchId, at), nameof(OnTeammateAbandoned));
    }
    public void OnForfeitVote(ActiveMatch match, PlayerKey voter, ForfeitVote vote, DateTimeOffset at)
    {
        Fan(l => l.OnForfeitVote(match, voter, vote, at), nameof(OnForfeitVote));
    }
    public void OnPlayerReleasedFromMatch(PlayerKey p, Guid matchId, DateTimeOffset at)
    {
        Fan(l => l.OnPlayerReleasedFromMatch(p, matchId, at), nameof(OnPlayerReleasedFromMatch));
    }
    public void OnPlayerDeparted(ActiveMatch match, PlayerKey p, DateTimeOffset returnBy, DateTimeOffset at)
    {
        Fan(l => l.OnPlayerDeparted(match, p, returnBy, at), nameof(OnPlayerDeparted));
    }
    public void OnPlayerReturnedToMatch(PlayerKey p, Guid matchId, DateTimeOffset at)
    {
        Fan(l => l.OnPlayerReturnedToMatch(p, matchId, at), nameof(OnPlayerReturnedToMatch));
    }
    public void OnTeamCollapsing(ActiveMatch m, int teamIdx, DateTimeOffset since, DateTimeOffset forfeitAt)
    {
        Fan(l => l.OnTeamCollapsing(m, teamIdx, since, forfeitAt), nameof(OnTeamCollapsing));
    }
    public void OnTeamRecovered(ActiveMatch m, int teamIdx)
    {
        Fan(l => l.OnTeamRecovered(m, teamIdx), nameof(OnTeamRecovered));
    }
    public void OnZoneVacated(ActiveMatch m, int teamIdx, DateTimeOffset since, DateTimeOffset forfeitAt)
    {
        Fan(l => l.OnZoneVacated(m, teamIdx, since, forfeitAt), nameof(OnZoneVacated));
    }
    public void OnZoneReclaimed(ActiveMatch m, int teamIdx)
    {
        Fan(l => l.OnZoneReclaimed(m, teamIdx), nameof(OnZoneReclaimed));
    }
    public void OnGriefingFlagged(PendingGriefingPenalty pending, DateTimeOffset timeoutUntil)
    {
        Fan(l => l.OnGriefingFlagged(pending, timeoutUntil), nameof(OnGriefingFlagged));
    }
    public void OnVetoRecorded(PendingGriefingPenalty pending, PlayerKey voter)
    {
        Fan(l => l.OnVetoRecorded(pending, voter), nameof(OnVetoRecorded));
    }
    public void OnGriefingVetoed(PendingGriefingPenalty pending)
    {
        Fan(l => l.OnGriefingVetoed(pending), nameof(OnGriefingVetoed));
    }
    public void OnGriefingConfirmed(PendingGriefingPenalty pending, DateTimeOffset timeoutUntil)
    {
        Fan(l => l.OnGriefingConfirmed(pending, timeoutUntil), nameof(OnGriefingConfirmed));
    }
    public void OnGroupDisbanded(IReadOnlyCollection<PlayerKey> notify, PlayerKey trigger, DisbandReason reason)
    {
        Fan(l => l.OnGroupDisbanded(notify, trigger, reason), nameof(OnGroupDisbanded));
    }
    public void OnInviteSent(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at, TimeSpan ttl)
    {
        Fan(l => l.OnInviteSent(inviter, invitee, at, ttl), nameof(OnInviteSent));
    }
    public void OnInviteAccepted(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        Fan(l => l.OnInviteAccepted(inviter, invitee, at), nameof(OnInviteAccepted));
    }
    public void OnInviteDeclined(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        Fan(l => l.OnInviteDeclined(inviter, invitee, at), nameof(OnInviteDeclined));
    }
    public void OnInviteExpired(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        Fan(l => l.OnInviteExpired(inviter, invitee, at), nameof(OnInviteExpired));
    }
}
