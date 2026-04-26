using System;
using System.Collections.Generic;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Eligibility;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Ratings;

namespace ClashEngine.Core;

/// <summary>
/// Top-level facade. Holds queue registry, multi-queue index, in-flight matches, ratings,
/// penalties, eligibility, and telemetry. The adapter (iteration #2) drives this on the
/// SubspaceServer mainloop thread by translating callbacks into the methods on this class.
/// </summary>
public sealed class MatchmakingEngine
{
    private readonly QueueRegistry _queues;
    private readonly MultiQueueIndex _multiQueue = new();
    private readonly TeamBalancer _balancer = new();
    private readonly IMatchQualityFunction _quality;
    private readonly Matcher _matcher;
    private readonly PenaltyTracker _penalties;
    private readonly PlayerEligibility _eligibility;
    private readonly IRatingStore _ratings;
    private readonly RatingUpdater _ratingUpdater;
    private readonly IClock _clock;
    private IMatchmakingTelemetry _telemetry;
    private readonly TimeSpan _joinTimeout;
    private readonly TimeSpan _graceWindow;
    private readonly Dictionary<Guid, ActiveMatch> _matches = new();
    private readonly Dictionary<Guid, QueueDefinition> _matchQueue = new();
    private readonly Dictionary<PlayerKey, Guid> _matchOf = new();
    private readonly HashSet<PlayerKey> _connected = new();
    private readonly Dictionary<(Guid MatchId, PlayerKey Target), PendingGriefingPenalty> _pendingGriefs = new();
    private readonly GroupRegistry _groups;

    // Per-(player, queue) consecutive-defense count for KOTH queues. Reset to 0 on a loss or
    // when the player isn't a winner.
    private readonly Dictionary<(PlayerKey Player, string QueueName), int> _consecutiveDefenses = new();

    public MatchmakingEngine(
        IRatingStore ratings,
        IClock clock,
        IEnumerable<PenaltyPolicy> penaltyPolicies,
        IMatchQualityFunction? quality = null,
        IMatchmakingTelemetry? telemetry = null,
        TimeSpan? joinTimeout = null,
        TimeSpan? graceWindow = null,
        TimeSpan? invitationTtl = null,
        RatingUpdater? ratingUpdater = null)
    {
        ArgumentNullException.ThrowIfNull(ratings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(penaltyPolicies);

        _ratings = ratings;
        _clock = clock;
        _penalties = new PenaltyTracker(penaltyPolicies);
        _quality = quality ?? new OrdinalSpreadQuality();
        _telemetry = telemetry ?? NoOpTelemetry.Instance;
        _joinTimeout = joinTimeout ?? TimeSpan.FromMinutes(1);
        _graceWindow = graceWindow ?? TimeSpan.FromSeconds(30);
        _ratingUpdater = ratingUpdater ?? new RatingUpdater();
        _queues = new QueueRegistry();
        _matcher = new Matcher(_queues, _multiQueue, _balancer, _quality, _clock);
        _eligibility = new PlayerEligibility(_penalties, _clock);
        _groups = new GroupRegistry(invitationTtl ?? TimeSpan.FromSeconds(15));
    }

    public QueueRegistry Queues => _queues;
    public PenaltyTracker Penalties => _penalties;
    public GroupRegistry Groups => _groups;
    public IReadOnlyDictionary<(Guid MatchId, PlayerKey Target), PendingGriefingPenalty> PendingGriefingPenalties => _pendingGriefs;
    public IRatingStore Ratings => _ratings;
    public IReadOnlyDictionary<Guid, ActiveMatch> ActiveMatches => _matches;

    /// <summary>
    /// Replace the telemetry sink at runtime. Used by the host to swap in a real listener
    /// composite after the engine is constructed and the listener objects (which depend on the
    /// engine reference) have been built.
    /// </summary>
    public void SetTelemetry(IMatchmakingTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        _telemetry = telemetry;
    }

    public bool IsInActiveMatch(PlayerKey player) => _matchOf.ContainsKey(player);

    public bool IsConnected(PlayerKey player) => _connected.Contains(player);

    public EligibilityResult CheckEligibility(PlayerKey player) =>
        _eligibility.Check(player, IsConnected(player), IsInActiveMatch(player));

    /// <summary>Marks a player as connected to the zone.</summary>
    public void OnPlayerConnected(PlayerKey player, DateTimeOffset at) => _connected.Add(player);

    /// <summary>
    /// Marks a player as disconnected. If they are in a queue they are removed; if they are in a
    /// match they begin the abandonment grace clock.
    /// </summary>
    public void OnPlayerDisconnected(PlayerKey player, DateTimeOffset at)
    {
        _connected.Remove(player);
        _matcher.DequeueEverywhere(player);
        // SubspaceServer cannot reliably distinguish a clean quit from a network drop at this
        // layer, so every departure is funneled through the same OnPlayerLeft path. The grace
        // window doubles as the "return window"; failure to return there is what flags abandonment.
        if (_matchOf.TryGetValue(player, out var matchId))
        {
            var m = _matches[matchId];
            var prev = SnapshotCollapsed(m);
            m.OnPlayerLeft(player, at);
            DiffCollapsedAndEmit(m, prev);
        }
    }

    /// <summary>Player has entered the assigned arena and is on their assigned ship/freq.</summary>
    public void OnPlayerJoinedArena(PlayerKey player, DateTimeOffset at)
    {
        if (!_matchOf.TryGetValue(player, out var matchId)) return;
        var m = _matches[matchId];
        var prev = m.State;
        m.OnPlayerJoined(player, at);
        if (prev == MatchState.Forming && m.State == MatchState.Live)
            _telemetry.OnMatchStarted(m);
    }

    /// <summary>Player has left the arena or specced.</summary>
    public void OnPlayerLeftArena(PlayerKey player, DateTimeOffset at)
    {
        if (_matchOf.TryGetValue(player, out var matchId))
        {
            var m = _matches[matchId];
            var prev = SnapshotCollapsed(m);
            m.OnPlayerLeft(player, at);
            DiffCollapsedAndEmit(m, prev);
        }
        _matcher.DequeueEverywhere(player);
    }

    /// <summary>Player has returned to their assigned ship/freq within the grace window.</summary>
    public void OnPlayerReturned(PlayerKey player, DateTimeOffset at)
    {
        if (_matchOf.TryGetValue(player, out var matchId))
        {
            var m = _matches[matchId];
            var prev = SnapshotCollapsed(m);
            m.OnPlayerReturned(player, at);
            DiffCollapsedAndEmit(m, prev);
        }
    }

    /// <summary>Records a kill. The kill is credited only if both players are in the same active match.</summary>
    public void OnKill(PlayerKey killer, PlayerKey victim, DateTimeOffset at)
    {
        if (!_matchOf.TryGetValue(killer, out var killerMatch)) return;
        if (!_matchOf.TryGetValue(victim, out var victimMatch)) return;
        if (killerMatch != victimMatch) return;

        var m = _matches[killerMatch];
        var prevCollapsed = SnapshotCollapsed(m);
        m.OnKill(killer, victim, at);
        DiffCollapsedAndEmit(m, prevCollapsed);

        // If the kill just eliminated the victim (lives = 0), release them from the match-roster
        // so they can queue elsewhere after a brief cooldown. They stay rostered in the
        // ActiveMatch for end-of-match rating purposes.
        if (m.LivesPerPlayer.HasValue
            && m.LivesRemaining.TryGetValue(victim, out var lives)
            && lives == 0
            && _matchOf.TryGetValue(victim, out var stillMappedMatch)
            && stillMappedMatch == killerMatch)
        {
            _matchOf.Remove(victim);
            if (_penalties.HasPolicy(PenaltyKind.EliminationCooldown))
                _penalties.RecordPenalty(victim, PenaltyKind.EliminationCooldown, at);
        }

        if (HasFinished(m)) FinalizeMatch(m, at);
    }

    /// <summary>
    /// Adds <paramref name="player"/> to <paramref name="queueName"/> if eligible. Returns
    /// <see langword="true"/> on success.
    /// </summary>
    public EnqueueResult TryEnqueue(PlayerKey player, string queueName, DateTimeOffset at)
    {
        if (!_queues.TryGet(queueName, out var def))
            return EnqueueResult.UnknownQueue;

        var elig = CheckEligibility(player);
        var status = elig.Status switch
        {
            EligibilityStatus.Disconnected => EnqueueResult.NotConnected,
            EligibilityStatus.InMatch => EnqueueResult.InMatch,
            EligibilityStatus.InTimeout => EnqueueResult.InTimeout,
            _ => EnqueueResult.Ok,
        };
        if (status != EnqueueResult.Ok) return status;

        var rating = _ratings.Get(player, def.GameType);
        if (!_matcher.Enqueue(player, rating, queueName))
            return EnqueueResult.AlreadyQueued;

        _telemetry.OnQueueAdded(player, queueName, at);
        return EnqueueResult.Ok;
    }

    /// <summary>
    /// Sends a group invitation. Returns <see cref="InviteResult.Sent"/> on success.
    /// </summary>
    public InviteResult InviteToGroup(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at) =>
        _groups.Invite(inviter, invitee, at);

    /// <summary>
    /// Accepts a pending invitation. If <paramref name="inviter"/> is null, the invitee must have
    /// exactly one pending invitation, otherwise <see cref="AcceptResult.AmbiguousMustSpecify"/>.
    /// On success the resulting <see cref="GroupId"/> is returned via <paramref name="groupId"/>.
    /// Membership change sweeps every member out of every queue (their now-stale individual or
    /// older-group entries no longer reflect the current group composition).
    /// </summary>
    public AcceptResult AcceptInvite(PlayerKey invitee, PlayerKey? inviter, DateTimeOffset at, out GroupId groupId)
    {
        var result = _groups.Accept(invitee, inviter, at, out groupId);
        if (result == AcceptResult.Joined)
            DequeueAllMembers(_groups.MembersOf(groupId), at);
        return result;
    }

    /// <summary>Declines a pending invitation. <paramref name="inviter"/> may be null when there's only one pending.</summary>
    public DeclineResult DeclineInvite(PlayerKey invitee, PlayerKey? inviter, DateTimeOffset at) =>
        _groups.Decline(invitee, inviter, at);

    /// <summary>
    /// Toggles the calling player's group between Open (any member can invite) and Closed
    /// (leader-only invites; leader-leave disbands). See <see cref="GroupRegistry.SetMode"/> for
    /// the per-mode permission rules.
    /// </summary>
    public SetModeResult SetGroupMode(PlayerKey caller, GroupMode newMode, DateTimeOffset at) =>
        _groups.SetMode(caller, newMode);

    /// <summary>
    /// Removes a player from their current group. Any membership change sweeps every removed
    /// and every surviving member from every queue (group-tagged queue entries are now stale)
    /// and, when the group dissolves, fires <see cref="IMatchmakingTelemetry.OnGroupDisbanded"/>
    /// so the adapter can chat-notify the surviving members.
    /// </summary>
    public bool LeaveGroup(PlayerKey player, DateTimeOffset at)
    {
        if (!_groups.Leave(player, out var outcome)) return false;

        // Sweep queues for everyone the change touches: leaver + (surviving members | freshly
        // dropped peers). The leaver is always in RemovedMembers; SurvivingMembers is non-empty
        // only when the group survives the leave.
        DequeueAllMembers(outcome.RemovedMembers, at);
        DequeueAllMembers(outcome.SurvivingMembers, at);

        if (outcome.GroupDissolved)
        {
            // Notify-set excludes the player who triggered the leave -- their command handler
            // (or the auto-drop path) will message them directly.
            var notify = new List<PlayerKey>(outcome.RemovedMembers.Count);
            foreach (var m in outcome.RemovedMembers)
                if (!m.Equals(player)) notify.Add(m);
            _telemetry.OnGroupDisbanded(notify, player, outcome.Reason);
        }
        return true;
    }

    /// <summary>Sweep every player in <paramref name="players"/> out of every queue, firing
    /// <see cref="IMatchmakingTelemetry.OnQueueRemoved"/> for each (player, queue) pair.</summary>
    private void DequeueAllMembers(IEnumerable<PlayerKey> players, DateTimeOffset at)
    {
        foreach (var p in players)
        {
            var queues = _matcher.DequeueEverywhere(p);
            for (int i = 0; i < queues.Count; i++)
                _telemetry.OnQueueRemoved(p, queues[i], at);
        }
    }

    /// <summary>
    /// Atomically enqueues a group of players into <paramref name="queueName"/>. All members must
    /// be eligible and not already queued; on any failure, no players are enqueued and no telemetry
    /// is emitted. Generates a fresh <see cref="GroupId"/> on success and returns it via
    /// <paramref name="groupId"/>; pass that ID to enqueue the same logical group into another queue.
    /// </summary>
    public EnqueueResult TryEnqueueGroup(
        IReadOnlyList<PlayerKey> members,
        string queueName,
        DateTimeOffset at,
        out GroupId groupId,
        GroupId? existingGroup = null)
    {
        groupId = default;
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0) return EnqueueResult.AlreadyQueued;
        if (!_queues.TryGet(queueName, out var def)) return EnqueueResult.UnknownQueue;

        // Hard rule: a group must fit entirely on one team in this queue. Otherwise the matcher
        // would always be forced to split it, defeating the purpose of grouping.
        if (members.Count > def.Shape.PlayersPerTeam) return EnqueueResult.GroupTooLarge;

        // Validate every member up-front. Fail fast on the worst status encountered.
        for (int i = 0; i < members.Count; i++)
        {
            var elig = CheckEligibility(members[i]);
            var status = elig.Status switch
            {
                EligibilityStatus.Disconnected => EnqueueResult.NotConnected,
                EligibilityStatus.InMatch => EnqueueResult.InMatch,
                EligibilityStatus.InTimeout => EnqueueResult.InTimeout,
                _ => EnqueueResult.Ok,
            };
            if (status != EnqueueResult.Ok) return status;
            if (def.Queue.Contains(members[i])) return EnqueueResult.AlreadyQueued;
        }

        // Reject duplicate keys within the group.
        var seen = new HashSet<PlayerKey>();
        for (int i = 0; i < members.Count; i++)
            if (!seen.Add(members[i])) return EnqueueResult.AlreadyQueued;

        // Prefer the inviter's existing GroupId from GroupRegistry over a fresh one.
        groupId = existingGroup
            ?? _groups.GroupOf(members[0])
            ?? GroupId.New();

        for (int i = 0; i < members.Count; i++)
        {
            var p = members[i];
            var rating = _ratings.Get(p, def.GameType);
            _matcher.Enqueue(p, rating, queueName, groupId);
            _telemetry.OnQueueAdded(p, queueName, at);
        }
        return EnqueueResult.Ok;
    }

    /// <summary>
    /// Cancels a Forming match because one or more players failed the idle/readiness check after
    /// being warped to the match arena. The named players are flagged as candidate-abandoners
    /// (with optional <paramref name="severityMultiplier"/>); the match transitions to Cancelled
    /// and other participants are released without penalty.
    /// </summary>
    public bool CancelMatchAsAfk(
        Guid matchId,
        IReadOnlyList<PlayerKey> afkPlayers,
        DateTimeOffset at,
        double severityMultiplier = 2.0)
    {
        if (!_matches.TryGetValue(matchId, out var match)) return false;
        if (match.State != MatchState.Forming) return false;

        // Drive the join-timeout path so the engine produces a Cancelled outcome with AFK
        // players in AbandonedBy. Players still in Pending status will be flagged as no-shows.
        match.Tick(match.ProposedAt + match.JoinTimeout + TimeSpan.FromSeconds(1));

        // Apply severity boost to the AFK players' abandonment records.
        if (severityMultiplier > 1.0 && match.Outcome is { } outcome)
        {
            foreach (var p in outcome.AbandonedBy)
            {
                if (afkPlayers.Contains(p))
                {
                    _penalties.RescindMostRecent(p, PenaltyKind.Abandonment);
                    _penalties.RecordPenalty(p, PenaltyKind.Abandonment, at, severityMultiplier);
                }
            }
        }

        FinalizeMatch(match, at);
        return true;
    }

    /// <summary>Removes a player from <paramref name="queueName"/> if they were enqueued.</summary>
    public bool Dequeue(PlayerKey player, string queueName, DateTimeOffset at)
    {
        if (!_matcher.Dequeue(player, queueName)) return false;
        _telemetry.OnQueueRemoved(player, queueName, at);
        return true;
    }

    /// <summary>Removes a player from every queue they are searching in.</summary>
    public IReadOnlyList<string> DequeueEverywhere(PlayerKey player, DateTimeOffset at)
    {
        var names = _matcher.DequeueEverywhere(player);
        for (int i = 0; i < names.Count; i++)
            _telemetry.OnQueueRemoved(player, names[i], at);
        return names;
    }

    /// <summary>
    /// Drives time-based transitions: ticks every active match (which may finalize), then tries
    /// to propose new matches from the queues.
    /// </summary>
    public void Tick(DateTimeOffset at)
    {
        List<ActiveMatch>? toFinalize = null;
        List<(ActiveMatch Match, Dictionary<int, DateTimeOffset> Prev)>? collapseSnapshots = null;
        foreach (var m in _matches.Values)
        {
            // Snapshot collapse map before Tick so we can emit collapse/recovery events.
            (collapseSnapshots ??= new()).Add((m, SnapshotCollapsed(m)));
            m.Tick(at);
            if (HasFinished(m)) (toFinalize ??= new List<ActiveMatch>()).Add(m);
        }
        if (collapseSnapshots is not null)
            foreach (var (m, prev) in collapseSnapshots)
                if (!HasFinished(m)) DiffCollapsedAndEmit(m, prev);
        if (toFinalize is not null)
            foreach (var m in toFinalize) FinalizeMatch(m, at);

        ExpireVetoWindows(at);
        _groups.PruneExpiredInvitations(at);

        while (true)
        {
            var proposal = _matcher.TryProposeMatch();
            if (proposal is null) break;
            FormMatchFromProposal(proposal, at);
        }
    }

    private void ExpireVetoWindows(DateTimeOffset at)
    {
        if (_pendingGriefs.Count == 0) return;
        List<(Guid, PlayerKey)>? expired = null;
        foreach (var kvp in _pendingGriefs)
        {
            if (kvp.Value.VetoWindowEndsAt <= at)
                (expired ??= new List<(Guid, PlayerKey)>()).Add(kvp.Key);
        }
        if (expired is null) return;
        foreach (var key in expired)
        {
            var pending = _pendingGriefs[key];
            _pendingGriefs.Remove(key);
            _telemetry.OnGriefingConfirmed(pending);
        }
    }

    /// <summary>
    /// Records a veto by <paramref name="voter"/> against the pending griefing penalty for
    /// <paramref name="target"/> in match <paramref name="matchId"/>. If the threshold is reached
    /// the penalty is rescinded.
    /// </summary>
    public VetoResult Veto(Guid matchId, PlayerKey target, PlayerKey voter, DateTimeOffset at)
    {
        var key = (matchId, target);
        if (!_pendingGriefs.TryGetValue(key, out var pending))
            return VetoResult.NoPendingPenalty;

        if (pending.VetoWindowEndsAt <= at)
        {
            _pendingGriefs.Remove(key);
            _telemetry.OnGriefingConfirmed(pending);
            return VetoResult.WindowExpired;
        }

        if (voter == target || !pending.EligibleVoters.Contains(voter))
            return VetoResult.NotEligible;

        if (pending.VotesReceived.Contains(voter))
            return VetoResult.AlreadyVoted;

        var newVotes = new HashSet<PlayerKey>(pending.VotesReceived) { voter };
        var updated = pending with { VotesReceived = newVotes };

        if (newVotes.Count >= pending.VetoesRequired)
        {
            _penalties.RescindMostRecent(target, PenaltyKind.Griefing);
            _pendingGriefs.Remove(key);
            _telemetry.OnGriefingVetoed(updated);
            return VetoResult.PenaltyRescinded;
        }

        _pendingGriefs[key] = updated;
        _telemetry.OnVetoRecorded(updated, voter);
        return VetoResult.RecordedNeedMore;
    }

    private void FormMatchFromProposal(MatchProposal proposal, DateTimeOffset at)
    {
        if (!_queues.TryGet(proposal.QueueName, out var def)) return;

        var matchId = Guid.NewGuid();
        var match = new ActiveMatch(
            matchId,
            def.GameType,
            proposal.Teams,
            def.EndPolicyFactory(),
            _joinTimeout,
            _graceWindow,
            at,
            livesPerPlayer: def.LivesPerPlayer,
            teamCollapseGrace: def.TeamCollapseGrace);
        _matches[matchId] = match;
        _matchQueue[matchId] = def;

        for (int t = 0; t < proposal.Teams.Count; t++)
            for (int j = 0; j < proposal.Teams[t].Count; j++)
                _matchOf[proposal.Teams[t][j]] = matchId;

        _telemetry.OnMatchProposed(proposal);
    }

    private void FinalizeMatch(ActiveMatch m, DateTimeOffset at)
    {
        if (m.Outcome is null) return;

        double weight = _matchQueue.TryGetValue(m.MatchId, out var queueDef) ? queueDef.RatingWeight : 1.0;

        if (m.Outcome.FinalState != MatchState.Cancelled)
            _ratingUpdater.ApplyOutcome(_ratings, m.Outcome, at, weight);

        for (int i = 0; i < m.Outcome.AbandonedBy.Count; i++)
        {
            var p = m.Outcome.AbandonedBy[i];
            int count = _penalties.RecordPenalty(p, PenaltyKind.Abandonment, at);
            var until = _penalties.TimeoutUntil(p)!.Value;
            _telemetry.OnAbandonment(p, count, until);
        }

        // Run griefing heuristic for non-cancelled matches and stage pending penalties.
        if (m.Outcome.FinalState != MatchState.Cancelled
            && queueDef is not null
            && _penalties.HasPolicy(PenaltyKind.Griefing))
        {
            FlagGriefers(m, queueDef, at);
        }

        for (int t = 0; t < m.Teams.Count; t++)
        {
            for (int j = 0; j < m.Teams[t].Count; j++)
            {
                var p = m.Teams[t][j];
                _matchOf.Remove(p);
                // Match is over -- release any active elimination cooldown so eliminated players
                // don't sit idle longer than the match itself ran.
                if (_penalties.HasPolicy(PenaltyKind.EliminationCooldown))
                    _penalties.RescindMostRecent(p, PenaltyKind.EliminationCooldown);
            }
        }

        _matches.Remove(m.MatchId);
        _matchQueue.Remove(m.MatchId);

        // KOTH re-enqueue: winners go to the head of the queue, capped by MaxConsecutiveDefenses.
        if (queueDef is { PromoteWinnersToFront: true }
            && m.Outcome.FinalState == MatchState.Completed
            && m.Outcome.RankedTeams.Count > 0)
        {
            ApplyKothReenqueue(queueDef, m.Outcome, at);
        }

        _telemetry.OnMatchEnded(m.Outcome);
    }

    private void ApplyKothReenqueue(QueueDefinition queue, MatchOutcome outcome, DateTimeOffset at)
    {
        var winners = outcome.RankedTeams[0].Players;
        var winningSet = new HashSet<PlayerKey>(winners);

        // Reset losers' defense counters.
        for (int r = 1; r < outcome.RankedTeams.Count; r++)
        {
            foreach (var p in outcome.RankedTeams[r].Players)
                _consecutiveDefenses.Remove((p, queue.Name));
        }

        // Determine whether winners exceed the cap.
        bool atLeastOneAtCap = false;
        foreach (var p in winners)
        {
            int prior = _consecutiveDefenses.TryGetValue((p, queue.Name), out var c) ? c : 0;
            if (prior + 1 > queue.MaxConsecutiveDefenses)
            {
                atLeastOneAtCap = true;
                break;
            }
        }

        // If any winner has hit the defense cap, send them all to the back and reset counters.
        // Otherwise, increment counters and re-enqueue at the head.
        foreach (var p in winners)
        {
            if (!_connected.Contains(p)) continue;   // disconnected winners forfeit their priority
            if (CheckEligibility(p).Status == EligibilityStatus.InTimeout) continue;

            var rating = _ratings.Get(p, queue.GameType);
            var groupId = _groups.GroupOf(p);

            if (atLeastOneAtCap)
            {
                _consecutiveDefenses.Remove((p, queue.Name));
                _matcher.Enqueue(p, rating, queue.Name, groupId);
            }
            else
            {
                int prior = _consecutiveDefenses.TryGetValue((p, queue.Name), out var c) ? c : 0;
                _consecutiveDefenses[(p, queue.Name)] = prior + 1;
                _matcher.EnqueuePriority(p, rating, queue.Name, groupId);
            }
            _telemetry.OnQueueAdded(p, queue.Name, at);
        }
    }

    private void FlagGriefers(ActiveMatch m, QueueDefinition def, DateTimeOffset at)
    {
        var heuristic = def.GriefingHeuristicFactory();
        var flags = heuristic.Evaluate(m);
        if (flags.Count == 0) return;

        var allParticipants = new HashSet<PlayerKey>();
        for (int t = 0; t < m.Teams.Count; t++)
            for (int j = 0; j < m.Teams[t].Count; j++)
                allParticipants.Add(m.Teams[t][j]);

        foreach (var flag in flags)
        {
            // Skip if heuristic flagged someone not in the match (defensive).
            if (!allParticipants.Contains(flag.Player)) continue;

            _penalties.RecordPenalty(flag.Player, PenaltyKind.Griefing, at, flag.Severity);

            var eligibleVoters = new HashSet<PlayerKey>(allParticipants);
            eligibleVoters.Remove(flag.Player);

            // Skip the veto window if there aren't enough eligible voters to ever rescind.
            if (eligibleVoters.Count < def.VetoesRequired)
                continue;

            var pending = new PendingGriefingPenalty(
                MatchId: m.MatchId,
                Target: flag.Player,
                Reason: flag.Reason,
                PenaltyAppliedAt: at,
                VetoWindowEndsAt: at + def.VetoWindow,
                VetoesRequired: def.VetoesRequired,
                EligibleVoters: eligibleVoters,
                VotesReceived: new HashSet<PlayerKey>());

            _pendingGriefs[(m.MatchId, flag.Player)] = pending;
            _telemetry.OnGriefingFlagged(pending);
        }
    }

    private static bool HasFinished(ActiveMatch m) =>
        m.State == MatchState.Completed
        || m.State == MatchState.Abandoned
        || m.State == MatchState.Cancelled;

    /// <summary>Captures the current per-team collapse timestamps so a follow-up call to
    /// <see cref="DiffCollapsedAndEmit"/> can emit telemetry for transitions.</summary>
    private static Dictionary<int, DateTimeOffset> SnapshotCollapsed(ActiveMatch m)
    {
        if (m.TeamCollapsedSince.Count == 0) return EmptyCollapseSnapshot;
        return new Dictionary<int, DateTimeOffset>(m.TeamCollapsedSince);
    }

    private static readonly Dictionary<int, DateTimeOffset> EmptyCollapseSnapshot = new();

    /// <summary>
    /// Emits <see cref="IMatchmakingTelemetry.OnTeamCollapsing"/> for any team that just entered
    /// collapse, and <see cref="IMatchmakingTelemetry.OnTeamRecovered"/> for any team that left it.
    /// </summary>
    private void DiffCollapsedAndEmit(ActiveMatch m, IReadOnlyDictionary<int, DateTimeOffset> prev)
    {
        var curr = m.TeamCollapsedSince;
        foreach (var kvp in curr)
        {
            if (!prev.ContainsKey(kvp.Key))
                _telemetry.OnTeamCollapsing(m, kvp.Key, kvp.Value, kvp.Value + m.TeamCollapseGrace);
        }
        if (prev.Count == 0) return;
        foreach (var kvp in prev)
        {
            if (!curr.ContainsKey(kvp.Key))
                _telemetry.OnTeamRecovered(m, kvp.Key);
        }
    }
}

/// <summary>Outcome of a <see cref="MatchmakingEngine.TryEnqueue"/> call.</summary>
public enum EnqueueResult
{
    Ok,
    UnknownQueue,
    NotConnected,
    InMatch,
    InTimeout,
    AlreadyQueued,

    /// <summary>The group has more members than the queue's <c>PlayersPerTeam</c>.</summary>
    GroupTooLarge,
}
