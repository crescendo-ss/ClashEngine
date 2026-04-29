using System;
using System.Collections.Generic;
using ClashEngine.Adapter;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Adapter;

/// <summary>
/// Bridges <see cref="IMatchmakingTelemetry"/> events from the pure engine into chat messages
/// (player-facing notifications) and log entries (server diagnostics).
/// </summary>
public sealed class EngineEventListener : IMatchmakingTelemetry
{
    private const string LogCategory = nameof(EngineEventListener);

    private readonly IChat _chat;
    private readonly ILogManager _log;
    private readonly PlayerKeyResolver _resolver;
    private readonly ClashLog _verbose;
    private readonly QueueRegistry _queues;

    public EngineEventListener(IChat chat, ILogManager log, PlayerKeyResolver resolver, ClashLog verbose, QueueRegistry queues)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _verbose = verbose ?? throw new ArgumentNullException(nameof(verbose));
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
    }

    public void OnQueueAdded(PlayerKey player, string queueName, DateTimeOffset at)
    {
        if (_verbose.IsDebug) _verbose.Debug(LogCategory, $"QueueAdded: {player.Name} -> {queueName}");
        if (_resolver.Resolve(player) is { } p)
            _chat.SendMessage(p, FormatQueuedMessage(queueName));
    }

    /// <summary>
    /// Renders the "queued for X" reply with a (competitive) / (casual) tier prefix sourced from
    /// the queue's registered <see cref="QueueDefinition.Tier"/>. The tier suffix on the
    /// registered queue name (<c>_competitive</c>/<c>_casual</c>) is stripped from the display
    /// so the prefix isn't redundantly echoed (e.g. "Queued for competitive 4v4", not
    /// "Queued for competitive 4v4_competitive"). Falls back to the bare queue name if the
    /// queue isn't registered.
    /// </summary>
    private string FormatQueuedMessage(string queueName)
    {
        if (!_queues.TryGet(queueName, out var def))
            return $"Queued for {queueName}.";
        string tierLabel = def.Tier == MatchmakingTier.Casual ? "casual" : "competitive";
        string suffix = "_" + tierLabel;
        string display = queueName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? queueName[..^suffix.Length]
            : queueName;
        return $"Queued for {tierLabel} {display}.";
    }

    public void OnQueueRemoved(PlayerKey player, string queueName, DateTimeOffset at)
    {
        if (_verbose.IsDebug) _verbose.Debug(LogCategory, $"QueueRemoved: {player.Name} <- {queueName}");
        if (_resolver.Resolve(player) is { } p)
            _chat.SendMessage(p, $"Left {queueName} queue.");
    }

    public void OnMatchProposed(MatchProposal proposal)
    {
        // The actionable "Match found! Move or fire within Xs" message is sent by
        // MatchOrchestrator once the players are in their ships. Don't double-message here.
        _log.LogM(LogLevel.Info, LogCategory,
            $"Match proposed in queue '{proposal.QueueName}' (quality={proposal.Quality:F2}, " +
            $"{proposal.Teams.Count}x{proposal.Teams[0].Count}).");
    }

    public void OnMatchStarted(ActiveMatch match)
    {
        _log.LogM(LogLevel.Info, LogCategory, $"Match {match.MatchId:N} started.");
    }

    public void OnMatchEnded(MatchOutcome outcome)
    {
        _log.LogM(LogLevel.Info, LogCategory,
            $"Match {outcome.MatchId:N} ended ({outcome.FinalState}). Abandoners: {outcome.AbandonedBy.Count}.");
    }

    public void OnAbandonment(PlayerKey player, int offenseCount, DateTimeOffset timeoutUntil)
    {
        var p = _resolver.Resolve(player);
        var remaining = timeoutUntil - DateTimeOffset.UtcNow;
        _verbose.Info(LogCategory,
            $"Abandonment: {player.Name} offense#{offenseCount} timeoutUntil={timeoutUntil:HH:mm:ss} (~{Format(remaining)})");
        if (p is null) return;
        _chat.SendMessage(p,
            $"You abandoned a match (offense #{offenseCount}). Queue-locked for {Format(remaining)}.");
    }

    public void OnTeamCollapsing(ActiveMatch match, int teamIdx, DateTimeOffset since, DateTimeOffset forfeitAt)
    {
        _verbose.Info(LogCategory,
            $"Match {match.MatchId:N} team {teamIdx} collapsing -- forfeit at {forfeitAt:HH:mm:ss} unless they recover.");
    }

    public void OnTeamRecovered(ActiveMatch match, int teamIdx)
    {
        _verbose.Info(LogCategory, $"Match {match.MatchId:N} team {teamIdx} recovered before forfeit.");
    }

    public void OnGriefingFlagged(PendingGriefingPenalty pending)
    {
        var window = pending.VetoWindowEndsAt - pending.PenaltyAppliedAt;
        var target = _resolver.Resolve(pending.Target);
        var targetName = target?.Name ?? pending.Target.Name;

        foreach (var voter in pending.EligibleVoters)
        {
            if (_resolver.Resolve(voter) is { } p)
            {
                _chat.SendMessage(p,
                    $"{targetName} was flagged for griefing ({pending.Reason}). " +
                    $"Use ?veto {targetName} within {Format(window)} to overturn " +
                    $"({pending.VetoesRequired} vetoes needed).");
            }
        }

        if (target is not null)
        {
            _chat.SendMessage(target,
                $"You were flagged for griefing: {pending.Reason}. " +
                $"Other players can ?veto within {Format(window)}.");
        }
    }

    public void OnVetoRecorded(PendingGriefingPenalty pending, PlayerKey voter)
    {
        if (_resolver.Resolve(voter) is { } p)
        {
            _chat.SendMessage(p,
                $"Roger. Need {pending.VetoesRemaining} additional player(s) to also veto the penalty.");
        }
    }

    public void OnGriefingVetoed(PendingGriefingPenalty pending)
    {
        var targetName = _resolver.Resolve(pending.Target)?.Name ?? pending.Target.Name;
        foreach (var voter in pending.EligibleVoters)
        {
            if (_resolver.Resolve(voter) is { } p)
                _chat.SendMessage(p, $"Penalty for {targetName} was vetoed by {pending.VotesReceived.Count} players.");
        }
        if (_resolver.Resolve(pending.Target) is { } t)
            _chat.SendMessage(t, "Your griefing penalty was vetoed by your peers.");
    }

    public void OnGriefingConfirmed(PendingGriefingPenalty pending)
    {
        _log.LogM(LogLevel.Info, LogCategory,
            $"Griefing penalty for {pending.Target.Name} confirmed (no veto: {pending.VotesReceived.Count}/{pending.VetoesRequired}).");
    }

    public void OnGroupDisbanded(IReadOnlyCollection<PlayerKey> notify, PlayerKey trigger, DisbandReason reason)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"GroupDisbanded: trigger={trigger.Name} reason={reason} notify=[{string.Join(",", notify)}]");

        string message = reason switch
        {
            DisbandReason.LeaderLeft =>
                $"Your party leader {trigger.Name} left; the party has been disbanded.",
            DisbandReason.LastMemberDropped =>
                $"{trigger.Name} left; the party has been disbanded.",
            // MemberLeft on a surviving group fires through the LeaveParty command directly,
            // so we don't expect to see it here. Fall through to a generic line just in case.
            _ => $"{trigger.Name} left the party.",
        };
        foreach (var member in notify)
        {
            if (_resolver.Resolve(member) is { } p)
                _chat.SendMessage(p, message);
        }
    }

    private static string Format(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{(int)ts.TotalSeconds}s";
        if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{(int)ts.TotalHours}h {ts.Minutes}m";
    }
}
