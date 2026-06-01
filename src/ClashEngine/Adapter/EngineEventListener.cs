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
using SS.Core;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Adapter;

/// <summary>
/// Bridges <see cref="IMatchmakingTelemetry"/> events from the pure engine into chat messages
/// (player-facing notifications) and log entries (server diagnostics).
/// </summary>
public sealed class EngineEventListener : IMatchmakingTelemetry
{
    private const string LogCategory = nameof(EngineEventListener);

    /// <summary>Per-player floor on time between consecutive queue-status DMs.</summary>
    private static readonly TimeSpan MmStatusCooldown = TimeSpan.FromSeconds(20);

    /// <summary>Below this hold window, the matcher would pop before the player notices the message.</summary>
    private static readonly TimeSpan HoldStartMinWindow = TimeSpan.FromSeconds(3);

    private readonly IChat _chat;
    private readonly ILogManager _log;
    private readonly PlayerKeyResolver _resolver;
    private readonly ClashLog _verbose;
    private readonly QueueRegistry _queues;
    private readonly GroupRegistry _groups;
    private readonly IClock _clock;
    private readonly Dictionary<PlayerKey, DateTimeOffset> _lastMmStatusAt = new();

    // Post-match DMs are buffered during FinalizeMatch (which fires OnAbandonment ->
    // OnGriefingFlagged -> OnWinnerPromoted, then OnMatchEnded as its terminator) and drained
    // from OnMatchEnded so they land AFTER ClashStatsTelemetry broadcasts the statbox. Order
    // requires ClashModule to register this listener after _matchStatsTelemetry in
    // CompositeTelemetry. Buffers are unkeyed because FinalizeMatch is single-threaded -- the
    // next OnMatchEnded that fires is always the terminator for whatever pushed into them.
    private readonly List<(Player Recipient, string Message)> _pendingAbandonmentDms = new();
    private readonly List<(Player Recipient, string Message)> _pendingGriefingDms = new();
    private readonly List<(Player Recipient, string Message)> _pendingPromotionDms = new();
    private readonly List<(Player Recipient, string Message)> _pendingAutoQueueDms = new();

    public EngineEventListener(
        IChat chat,
        ILogManager log,
        PlayerKeyResolver resolver,
        ClashLog verbose,
        QueueRegistry queues,
        GroupRegistry groups,
        IClock clock)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _verbose = verbose ?? throw new ArgumentNullException(nameof(verbose));
        _queues = queues ?? throw new ArgumentNullException(nameof(queues));
        _groups = groups ?? throw new ArgumentNullException(nameof(groups));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Returns true and records "now" if <paramref name="player"/> hasn't received a queue-status
    /// DM within <see cref="MmStatusCooldown"/>; otherwise false (caller should suppress).
    /// </summary>
    private bool TryClaimMmStatusSlot(PlayerKey player)
    {
        var now = _clock.UtcNow;
        if (_lastMmStatusAt.TryGetValue(player, out var last) && now - last < MmStatusCooldown)
            return false;
        _lastMmStatusAt[player] = now;
        return true;
    }

    /// <summary>
    /// Returns true if <paramref name="player"/> should receive a per-group matchmaking DM:
    /// they're solo, or they are the leader of their current group. Non-leader group members are
    /// suppressed so we don't multi-DM the same notice across a party.
    /// </summary>
    private bool ShouldDmForGroup(PlayerKey player)
    {
        var group = _groups.GroupOf(player);
        if (group is null) return true;
        return _groups.IsLeader(player, group.Value);
    }

    /// <summary>
    /// Renders the operator-chosen <see cref="QueueDefinition.Label"/> for chat output. Falls
    /// back to the bare queue name when the queue isn't registered (which shouldn't happen for
    /// any name we receive from the engine).
    /// </summary>
    private string FormatQueueDescriptor(string queueName) =>
        _queues.TryGet(queueName, out var def) ? def.Label : queueName;

    public void OnQueueAdded(PlayerKey player, string queueName, DateTimeOffset at, PlayerKey? initiator = null)
    {
        if (_verbose.IsDebug) _verbose.Debug(LogCategory, $"QueueAdded: {player.Name} -> {queueName}");
        if (_resolver.Resolve(player) is { } p)
            _chat.SendMessage(p, FormatQueuedMessage(queueName, player, initiator));
    }

    /// <summary>
    /// Renders the "queued for X" reply using the queue's operator-chosen
    /// <see cref="QueueDefinition.Label"/>. Falls back to the bare queue name if the queue
    /// isn't registered.
    /// <para>When <paramref name="initiator"/> is set and differs from <paramref name="player"/>
    /// (open-party group enqueue, recipient row), attributes the queue to the initiating party
    /// member ("X queued you for ..."). Otherwise renders the standard "Queued for ..." reply.</para>
    /// </summary>
    private string FormatQueuedMessage(string queueName, PlayerKey player, PlayerKey? initiator)
    {
        string descriptor;
        int? waiting = null;
        if (_queues.TryGet(queueName, out var def))
        {
            descriptor = def.Label;
            // OnQueueAdded fires after the matcher has appended the player, so Count already
            // includes the freshly-enqueued addition.
            waiting = def.Queue.Count;
        }
        else
        {
            descriptor = queueName;
        }

        bool attributed = initiator is PlayerKey ini && !ini.Equals(player);
        string countSuffix = waiting is { } w ? $" ({w} in queue)" : "";
        return attributed
            ? $"{initiator!.Value.Name} queued you for {descriptor}{countSuffix}."
            : $"Queued for {descriptor}{countSuffix}.";
    }

    public void OnQueueRemoved(PlayerKey player, string queueName, DateTimeOffset at,
        QueueRemovalReason reason = QueueRemovalReason.Cancel)
    {
        if (_verbose.IsDebug) _verbose.Debug(LogCategory, $"QueueRemoved: {player.Name} <- {queueName} ({reason})");

        // Disconnect: the player is gone -- nothing to DM. Matched: the orchestrator owns the
        // "match found" messaging, so a "Left queue" line here would be noise. Both are removals
        // that did not surface to this listener before the reason param existed; keep them silent.
        if (reason is QueueRemovalReason.Disconnect or QueueRemovalReason.Matched) return;

        if (_resolver.Resolve(player) is not { } p) return;
        var descriptor = FormatQueueDescriptor(queueName);
        var message = reason == QueueRemovalReason.AfkCull
            ? $"Removed from {descriptor} -- you'd been waiting a while, so we assumed you stepped away. Use ?play to rejoin."
            : $"Left {descriptor} queue.";
        _chat.SendMessage(p, message);
    }

    public void OnQueueDwellWarning(PlayerKey player, string queueName, DateTimeOffset at, TimeSpan dwell)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory, $"QueueDwellWarning: {player.Name} in {queueName} for {Format(dwell)}");
        if (_resolver.Resolve(player) is { } p)
            _chat.SendMessage(p,
                $"You've been in {FormatQueueDescriptor(queueName)} for {Format(dwell)} -- still around? " +
                "Use ?play again to refresh, or you'll be removed for inactivity.");
    }

    public void OnDiscordLinkRequested(PlayerKey player, string discordAlias, DateTimeOffset at)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory, $"DiscordLinkRequested: {player.Name} -> {discordAlias}");
        if (_resolver.Resolve(player) is { } p)
            _chat.SendMessage(p,
                $"Sent a link request for Discord alias '{discordAlias}'. Check the Discord bot to confirm.");
    }

    public void OnWinnerPromoted(PlayerKey player, string queueName, DateTimeOffset at,
        int defensesUsed, int maxDefenses, bool sentToBack)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"WinnerPromoted: {player.Name} -> {queueName} (defense {defensesUsed}/{maxDefenses}, sentToBack={sentToBack})");
        if (_resolver.Resolve(player) is not { } p) return;

        var descriptor = FormatQueueDescriptor(queueName);
        int? waiting = _queues.TryGet(queueName, out var def) ? def.Queue.Count : null;
        var countSuffix = waiting is { } w ? $", {w} in queue" : "";
        var message = sentToBack
            ? $"Your win-streak exceeds the maximum allowed to be promoted to the front of the queue. Re-queued at the back of {descriptor}{countSuffix}."
            : $"Congrats! Autoqueued and promoted to be at the front of the line for {descriptor} (win-streak: {defensesUsed}).";
        _pendingPromotionDms.Add((p, message));
    }

    public void OnAutoQueued(PlayerKey player, string queueName, DateTimeOffset at)
    {
        if (_verbose.IsDebug) _verbose.Debug(LogCategory, $"AutoQueued: {player.Name} -> {queueName}");
        if (_resolver.Resolve(player) is not { } p) return;

        // "Auto-queued for {queue} ... back ..." is the contract the bot harness (ClashRig
        // RegressionZone) keys on: the queue label names which queue, "back" says where they landed.
        // The non-hyphenated KOTH "Autoqueued ... front of the line" message is deliberately distinct.
        var descriptor = FormatQueueDescriptor(queueName);
        _pendingAutoQueueDms.Add((p,
            $"Auto-queued for {descriptor} -- you're at the back of the line. " +
            "Use ?cancel to leave, or ?autoqueue off to stop."));
    }

    public void OnAutoQueueDisabledByAfk(PlayerKey player, DateTimeOffset at)
    {
        if (_verbose.IsDebug) _verbose.Debug(LogCategory, $"AutoQueueDisabledByAfk: {player.Name}");
        if (_resolver.Resolve(player) is not { } p) return;
        _pendingAutoQueueDms.Add((p,
            "Auto-queue was turned off because you were flagged AFK. Use ?autoqueue on to re-enable it."));
    }

    public void OnQueueNearFull(string queueName, IReadOnlyList<PlayerKey> waiting, int waitingCount, int needed)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory, $"QueueNearFull: {queueName} ({waitingCount}/{needed})");

        var descriptor = FormatQueueDescriptor(queueName);
        var message = $"{waitingCount}/{needed} in {descriptor} -- one more to go.";

        for (int i = 0; i < waiting.Count; i++)
        {
            var key = waiting[i];
            if (!ShouldDmForGroup(key)) continue;
            if (!TryClaimMmStatusSlot(key)) continue;
            if (_resolver.Resolve(key) is { } p)
                _chat.SendMessage(p, message);
        }
    }

    public void OnQueueMatchmakingBlocked(string queueName, QueueBlockStatus status, DateTimeOffset at)
    {
        // Verbose-only diagnostic: explain why a full-enough queue isn't starting. Fired on change
        // (see the matcher), so this is one line per transition, not per tick. No chat -- the same
        // explanation is available on demand via ?queue.
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory, $"QueueMatchmakingBlocked: {queueName} -- {QueueBlockMessage.Format(status, at)}");
    }

    public void OnQueueHoldStarted(string queueName, IReadOnlyList<PlayerKey> candidates, double currentQuality, TimeSpan holdWindow)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"QueueHoldStarted: {queueName} q={currentQuality:F2} window={Format(holdWindow)}");

        // Hold windows under a few seconds resolve before the player would notice the message --
        // skip to keep chat clean.
        if (holdWindow <= HoldStartMinWindow) return;

        var descriptor = FormatQueueDescriptor(queueName);
        var seconds = (int)Math.Ceiling(holdWindow.TotalSeconds);
        var message = $"{descriptor} matchmaking succeeded. Waiting up to {seconds}s to find the fairest split...";
        BroadcastToCandidates(candidates, message);
    }

    public void OnQueueHoldImproved(string queueName, IReadOnlyList<PlayerKey> candidates, double oldQuality, double newQuality)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"QueueHoldImproved: {queueName} {oldQuality:F2} -> {newQuality:F2}");

        var descriptor = FormatQueueDescriptor(queueName);
        var message = $"Better match-up found in {descriptor} -- locking in shortly.";
        BroadcastToCandidates(candidates, message);
    }

    /// <summary>
    /// DMs <paramref name="message"/> to each candidate, gated by the per-player cooldown and the
    /// party-leader-only rule (so a 4-player party gets one DM, not four).
    /// </summary>
    private void BroadcastToCandidates(IReadOnlyList<PlayerKey> candidates, string message)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            var key = candidates[i];
            if (!ShouldDmForGroup(key)) continue;
            if (!TryClaimMmStatusSlot(key)) continue;
            if (_resolver.Resolve(key) is { } p)
                _chat.SendMessage(p, message);
        }
    }

    public void OnInviteSent(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at, TimeSpan ttl)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory, $"InviteSent: {inviter.Name} -> {invitee.Name} (ttl={Format(ttl)})");
        if (_resolver.Resolve(invitee) is { } p)
            _chat.SendMessage(p,
                $"{inviter.Name} invited you to a party. Use ?accept {inviter.Name} within {Format(ttl)} or ?decline.");
    }

    public void OnInviteAccepted(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory, $"InviteAccepted: {invitee.Name} -> {inviter.Name}");
        // The invitee's own ?accept handler messages them ("You joined the group..."); just notify
        // the inviter so they know their invitation got picked up.
        if (_resolver.Resolve(inviter) is { } p)
            _chat.SendMessage(p, $"{invitee.Name} accepted your party invitation.");
    }

    public void OnInviteDeclined(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory, $"InviteDeclined: {invitee.Name} -> {inviter.Name}");
        if (_resolver.Resolve(inviter) is { } p)
            _chat.SendMessage(p, $"{invitee.Name} declined your party invitation.");
    }

    public void OnInviteExpired(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory, $"InviteExpired: {inviter.Name} -> {invitee.Name}");
        if (_resolver.Resolve(inviter) is { } p)
            _chat.SendMessage(p, $"Your party invitation to {invitee.Name} expired with no response.");
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

        // Drain post-match DMs queued during FinalizeMatch. ClashStatsTelemetry broadcast the
        // statbox earlier in this same OnMatchEnded fan-out (it's registered ahead of us in
        // CompositeTelemetry), so the player sees: kill feed -> statbox -> these DMs.
        DrainBuffered(_pendingAbandonmentDms);
        DrainBuffered(_pendingGriefingDms);
        DrainBuffered(_pendingPromotionDms);
        DrainBuffered(_pendingAutoQueueDms);
    }

    private void DrainBuffered(List<(Player Recipient, string Message)> buffer)
    {
        for (int i = 0; i < buffer.Count; i++)
            _chat.SendMessage(buffer[i].Recipient, buffer[i].Message);
        buffer.Clear();
    }

    public void OnAbandonment(PlayerKey player, int offenseCount, DateTimeOffset timeoutUntil)
    {
        var p = _resolver.Resolve(player);
        var remaining = timeoutUntil - DateTimeOffset.UtcNow;
        _verbose.Info(LogCategory,
            $"Abandonment: {player.Name} offense#{offenseCount} timeoutUntil={timeoutUntil:HH:mm:ss} (~{Format(remaining)})");
        if (p is null) return;
        _pendingAbandonmentDms.Add((p,
            $"You abandoned a match (offense #{offenseCount}). Queue-locked for {HumanDuration.Humanize(remaining)}."));
    }

    public void OnTeammateAbandoned(IReadOnlyCollection<PlayerKey> survivors, PlayerKey abandoner, Guid matchId, DateTimeOffset at)
    {
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"TeammateAbandoned: {abandoner.Name} abandoned match {matchId:N}; notifying {survivors.Count} survivor(s) they are free to leave.");
        // Sent live, the moment the abandon is assessed -- not buffered like the post-match DMs
        // above. The surviving teammate can keep playing or leave penalty-free, so they need to
        // know now, while the match is still running.
        foreach (var survivor in survivors)
        {
            if (_resolver.Resolve(survivor) is { } p)
                _chat.SendMessage(p,
                    $"Your teammate {abandoner.Name} abandoned the match -- you are free to leave without penalty.");
        }
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

    public void OnGriefingFlagged(PendingGriefingPenalty pending, DateTimeOffset timeoutUntil)
    {
        var penaltyDuration = timeoutUntil - pending.PenaltyAppliedAt;
        var penaltyMinutes = Math.Max(1, (int)Math.Round(penaltyDuration.TotalMinutes));
        var target = _resolver.Resolve(pending.Target);
        var targetName = target?.Name ?? pending.Target.Name;

        foreach (var voter in pending.EligibleVoters)
        {
            var resolved = _resolver.Resolve(voter);
            if (_verbose.IsDebug)
                _verbose.Debug(LogCategory,
                    $"GriefingFlagged voter dispatch: {voter.Name} resolved={(resolved is null ? "null" : "ok")}");
            if (resolved is { } p)
            {
                _pendingGriefingDms.Add((p,
                    $"{targetName} has been assessed with a {penaltyMinutes} minute griefing penalty. " +
                    $"Use ?forgive {targetName} to vote to have that penalty removed " +
                    $"({pending.VetoesRequired} votes needed)."));
            }
        }

        if (target is not null)
        {
            var window = pending.VetoWindowEndsAt - pending.PenaltyAppliedAt;
            _pendingGriefingDms.Add((target,
                $"You were flagged for griefing: {pending.Reason}. " +
                $"Other players can ?forgive within {Format(window)}."));
        }
    }

    public void OnVetoRecorded(PendingGriefingPenalty pending, PlayerKey voter)
    {
        if (_resolver.Resolve(voter) is { } p)
        {
            _chat.SendMessage(p,
                $"Roger. Need {pending.VetoesRemaining} additional player(s) to also forgive the penalty.");
        }
    }

    public void OnGriefingVetoed(PendingGriefingPenalty pending)
    {
        var targetName = _resolver.Resolve(pending.Target)?.Name ?? pending.Target.Name;
        foreach (var voter in pending.EligibleVoters)
        {
            if (_resolver.Resolve(voter) is { } p)
                _chat.SendMessage(p, $"Penalty for {targetName} was forgiven by {pending.VotesReceived.Count} players.");
        }
        if (_resolver.Resolve(pending.Target) is { } t)
            _chat.SendMessage(t, "Your griefing penalty was forgiven by your peers.");
    }

    public void OnGriefingConfirmed(PendingGriefingPenalty pending, DateTimeOffset timeoutUntil)
    {
        _log.LogM(LogLevel.Info, LogCategory,
            $"Griefing penalty for {pending.Target.Name} confirmed (no veto: {pending.VotesReceived.Count}/{pending.VetoesRequired}).");
        if (_resolver.Resolve(pending.Target) is { } t)
        {
            var remaining = timeoutUntil - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            _chat.SendMessage(t,
                $"You were penalized for griefing: {pending.Reason}. Queue-locked for {HumanDuration.Humanize(remaining)}.");
        }
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
