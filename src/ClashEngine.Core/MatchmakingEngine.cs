using System;
using System.Collections.Generic;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Eligibility;
using ClashEngine.Core.GameType;
using ClashEngine.Core.Groups;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Preferences;
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
    private readonly GameTypeRegistry _gameTypes = new();
    private readonly MultiQueueIndex _multiQueue = new();
    private readonly TeamBalancer _balancer = new();
    private readonly IMatchQualityFunction _quality;
    private readonly Matcher _matcher;
    private readonly PenaltyTracker _penalties;
    private readonly PlayerEligibility _eligibility;
    private readonly IRatingStore _ratings;
    private readonly IAutoQueueStore _autoQueue;
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

    // Names of queues whose "near-full" notification has already fired in the current fill cycle.
    // An entry is removed once the queue's count drops back below TotalPlayers - 1, re-arming it
    // for the next near-full transition. Only queues with TotalPlayers >= 4 ever participate.
    private readonly HashSet<string> _nearFullFired = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Minimum match shape (in total players) eligible for near-full chat notifications.</summary>
    private const int NearFullMinShape = 4;

    // (player, queueName) -> the QueueEntry.LastSeenAt we last fired an AFK dwell-warning for.
    // Keying on the liveness epoch means anything that stamps a fresh LastSeenAt -- a leave-and-
    // re-queue, or a present player re-issuing ?play (Matcher.Touch) -- re-arms the warning even
    // if no sweep observed the gap. Stale pairs (player no longer in the queue) are pruned at the
    // top of SweepAfkDwell to bound memory.
    private readonly Dictionary<(PlayerKey Player, string Queue), DateTimeOffset> _dwellWarned = new();

    public MatchmakingEngine(
        IRatingStore ratings,
        IClock clock,
        IEnumerable<PenaltyPolicy> penaltyPolicies,
        IMatchQualityFunction? quality = null,
        IMatchmakingTelemetry? telemetry = null,
        TimeSpan? joinTimeout = null,
        TimeSpan? graceWindow = null,
        TimeSpan? invitationTtl = null,
        RatingUpdater? ratingUpdater = null,
        IAutoQueueStore? autoQueue = null)
    {
        ArgumentNullException.ThrowIfNull(ratings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(penaltyPolicies);

        _ratings = ratings;
        _autoQueue = autoQueue ?? new InMemoryAutoQueueStore();
        _clock = clock;
        _penalties = new PenaltyTracker(penaltyPolicies);
        _quality = quality ?? new OrdinalSpreadQuality();
        _telemetry = telemetry ?? NoOpTelemetry.Instance;
        _joinTimeout = joinTimeout ?? TimeSpan.FromMinutes(1);
        _graceWindow = graceWindow ?? TimeSpan.FromSeconds(30);
        _ratingUpdater = ratingUpdater ?? new RatingUpdater();
        _queues = new QueueRegistry();
        // Telemetry-getter passthrough so the matcher always observes the engine's current sink,
        // including post-construction swaps via SetTelemetry.
        _matcher = new Matcher(_queues, _multiQueue, _balancer, _quality, _clock, () => _telemetry);
        _eligibility = new PlayerEligibility(_penalties, _clock);
        _groups = new GroupRegistry(invitationTtl ?? TimeSpan.FromSeconds(15));
    }

    public QueueRegistry Queues => _queues;
    public GameTypeRegistry GameTypes => _gameTypes;

    /// <summary>
    /// Why a full-enough queue (<paramref name="queueUniqueId"/>) has not been turned into a match,
    /// for the <c>?queue</c> diagnostic line. Returns <see langword="false"/> when the queue is
    /// under-filled, just popped a match, or is unknown -- i.e. there is nothing to explain.
    /// </summary>
    public bool TryGetQueueBlockStatus(string queueUniqueId, out Matching.QueueBlockStatus status) =>
        _matcher.TryGetBlockStatus(queueUniqueId, out status);
    public PenaltyTracker Penalties => _penalties;
    public GroupRegistry Groups => _groups;
    public IReadOnlyDictionary<(Guid MatchId, PlayerKey Target), PendingGriefingPenalty> PendingGriefingPenalties => _pendingGriefs;
    public IRatingStore Ratings => _ratings;

    /// <summary>
    /// The per-player auto-queue preference store (the <c>?autoqueue</c> switch). The adapter reads
    /// and writes this from the command handler; the engine consults it during match finalization
    /// to re-enqueue opted-in players.
    /// </summary>
    public IAutoQueueStore AutoQueue => _autoQueue;
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
        var removedQueues = _matcher.DequeueEverywhere(player);
        for (int i = 0; i < removedQueues.Count; i++)
            _telemetry.OnQueueRemoved(player, removedQueues[i], at, QueueRemovalReason.Disconnect);
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
        _matches[matchId].OnPlayerJoined(player, at);
    }

    /// <summary>
    /// Transition the match to Live. Called by the orchestrator at GO! (after Setup, Staging,
    /// and Countdown). Returns false if the match is unknown, no longer Forming, or some player
    /// hasn't reached Active yet -- callers should treat false as "match isn't ready, leave it
    /// to the engine's join-timeout to clean up." Fires <c>OnMatchStarted</c> on success.
    /// </summary>
    public bool MarkMatchLive(Guid matchId, DateTimeOffset at)
    {
        if (!_matches.TryGetValue(matchId, out var match)) return false;
        if (!match.MarkLive(at)) return false;
        _telemetry.OnMatchStarted(match);
        return true;
    }

    /// <summary>
    /// Player has left the arena outright (e.g. <c>?go pub</c>). Starts the abandonment grace
    /// clock if they were in a match. Does NOT touch queue membership -- a queued player who
    /// arena-hops keeps their spot. The only paths that drop a queue entry are <c>?cancel</c>,
    /// disconnect, match formation (matcher's automatic dequeue), and eligibility changes; the
    /// orchestrator's cross-arena warp on match formation therefore no longer self-dequeues here.
    /// </summary>
    public void OnPlayerLeftArena(PlayerKey player, DateTimeOffset at)
    {
        if (_matchOf.TryGetValue(player, out var matchId))
        {
            var m = _matches[matchId];
            var prev = SnapshotCollapsed(m);
            m.OnPlayerLeft(player, at);
            DiffCollapsedAndEmit(m, prev);
        }
    }

    /// <summary>
    /// Player ship-changed to spec without leaving the arena. Mirrors
    /// <see cref="OnPlayerLeftArena"/>'s in-match bookkeeping (so abandonment grace still runs)
    /// but deliberately does not dequeue: the orchestrator sends KOTH winners to spec on match
    /// cleanup, which would otherwise yank them right back out of the queue we just promoted
    /// them into. Players parked in spec while waiting for a match also stay queued -- only the
    /// explicit <c>?cancel</c> path or a real arena exit drops a queue entry.
    /// </summary>
    public void OnPlayerSpecced(PlayerKey player, DateTimeOffset at)
    {
        if (!_matchOf.TryGetValue(player, out var matchId)) return;
        var m = _matches[matchId];
        var prev = SnapshotCollapsed(m);
        m.OnPlayerLeft(player, at);
        DiffCollapsedAndEmit(m, prev);
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
    /// <remarks>
    /// Anchors on the victim's <see cref="_matchOf"/> entry. The killer may either be in the
    /// same <c>_matchOf</c> entry (the happy path) or be absent from <c>_matchOf</c> while still
    /// rostered in the match -- that handles a residual-weapon kill from a player who was just
    /// eliminated and is still on a ship inside the orchestrator's <c>KnockoutSpecDelay</c>
    /// window. Without that fall-through, simultaneous last-life eliminations dropped the
    /// second kill on the floor: the second victim's life never decremented, <c>_exitedAt</c>
    /// never set, and the freq advisor opened a ship-change grace window for them, letting
    /// them ship back up after their "final" death.
    /// </remarks>
    public void OnKill(PlayerKey killer, PlayerKey victim, DateTimeOffset at)
    {
        if (!_matchOf.TryGetValue(victim, out var matchId)) return;
        var m = _matches[matchId];

        if (_matchOf.TryGetValue(killer, out var killerMatch))
        {
            if (killerMatch != matchId) return;
        }
        else if (m.TeamIndexOf(killer) is null)
        {
            // Killer is not in any match and was never rostered in the victim's match -- reject
            // as a cross-match / unrelated kill.
            return;
        }

        var prevCollapsed = SnapshotCollapsed(m);
        m.OnKill(killer, victim, at);
        DiffCollapsedAndEmit(m, prevCollapsed);

        // If the kill just eliminated the victim (ExitedAt set), release them from the
        // match-roster so they can queue elsewhere after a brief cooldown. They stay rostered
        // in the ActiveMatch for end-of-match rating purposes.
        if (m.LivesPerPlayer.HasValue
            && m.ExitedAt.ContainsKey(victim)
            && _matchOf.TryGetValue(victim, out var stillMappedMatch)
            && stillMappedMatch == matchId)
        {
            _matchOf.Remove(victim);
            _telemetry.OnPlayerReleasedFromMatch(victim, matchId, at);
            // The cooldown length is the match's game type's EliminationCooldown:
            //   null     -> use the policy's built-in default (BaseTimeout),
            //   > 0       -> that duration, carried as a per-event base override,
            //   Zero      -> disabled for this game type: record nothing, requeue immediately.
            if (_penalties.HasPolicy(PenaltyKind.EliminationCooldown))
            {
                var cooldown = m.EliminationCooldown;
                if (cooldown is null)
                    _penalties.RecordPenalty(victim, PenaltyKind.EliminationCooldown, at);
                else if (cooldown.Value > TimeSpan.Zero)
                    _penalties.RecordPenalty(victim, PenaltyKind.EliminationCooldown, at,
                        baseTimeoutOverride: cooldown.Value);
            }
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
        {
            // Already queued here: treat the repeat ?play as a liveness ping. Refresh the AFK
            // dwell clock (LastSeenAt) -- leaving queue order, wait-based quality relaxation, and
            // the displayed wait (all keyed on EnqueuedAt) untouched -- so a present player can
            // keep themselves from being culled, exactly as the dwell warning instructs.
            _matcher.Touch(player, queueName, at);
            return EnqueueResult.AlreadyQueuedRefreshed;
        }

        _telemetry.OnQueueAdded(player, queueName, at);
        return EnqueueResult.Ok;
    }

    /// <summary>
    /// Sends a group invitation. Returns <see cref="InviteResult.Sent"/> on success and fires
    /// <see cref="IMatchmakingTelemetry.OnInviteSent"/> so the adapter can DM the invitee.
    /// </summary>
    public InviteResult InviteToGroup(PlayerKey inviter, PlayerKey invitee, DateTimeOffset at)
    {
        var result = _groups.Invite(inviter, invitee, at);
        if (result == InviteResult.Sent)
            _telemetry.OnInviteSent(inviter, invitee, at, _groups.InvitationTtl);
        return result;
    }

    /// <summary>
    /// Accepts a pending invitation. If <paramref name="inviter"/> is null, the invitee must have
    /// exactly one pending invitation, otherwise <see cref="AcceptResult.AmbiguousMustSpecify"/>.
    /// On success the resulting <see cref="GroupId"/> is returned via <paramref name="groupId"/>.
    /// Membership change sweeps every member out of every queue (their now-stale individual or
    /// older-group entries no longer reflect the current group composition).
    /// </summary>
    public AcceptResult AcceptInvite(PlayerKey invitee, PlayerKey? inviter, DateTimeOffset at, out GroupId groupId)
    {
        var result = _groups.Accept(invitee, inviter, at, out groupId, out var resolvedInviter);
        if (result == AcceptResult.Joined)
        {
            DequeueAllMembers(_groups.MembersOf(groupId), at);
            _telemetry.OnInviteAccepted(resolvedInviter, invitee, at);
        }
        return result;
    }

    /// <summary>Declines a pending invitation. <paramref name="inviter"/> may be null when there's only one pending.</summary>
    public DeclineResult DeclineInvite(PlayerKey invitee, PlayerKey? inviter, DateTimeOffset at)
    {
        var result = _groups.Decline(invitee, inviter, at, out var resolvedInviter);
        if (result == DeclineResult.Declined)
            _telemetry.OnInviteDeclined(resolvedInviter, invitee, at);
        return result;
    }

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
    private void DequeueAllMembers(IEnumerable<PlayerKey> players, DateTimeOffset at,
        QueueRemovalReason reason = QueueRemovalReason.GroupChange)
    {
        foreach (var p in players)
        {
            var queues = _matcher.DequeueEverywhere(p);
            for (int i = 0; i < queues.Count; i++)
                _telemetry.OnQueueRemoved(p, queues[i], at, reason);
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
        GroupId? existingGroup = null,
        PlayerKey? initiator = null)
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
            if (def.Queue.Contains(members[i]))
            {
                // Party already queued here -> the repeat ?play is a liveness ping for the whole
                // party. Refresh every member that's present (atomic enqueue keeps them together,
                // but touch defensively) and report the refresh rather than a bare AlreadyQueued.
                bool refreshed = false;
                for (int j = 0; j < members.Count; j++)
                    refreshed |= _matcher.Touch(members[j], queueName, at);
                return refreshed ? EnqueueResult.AlreadyQueuedRefreshed : EnqueueResult.AlreadyQueued;
            }
        }

        // Reject duplicate keys within the group.
        var seen = new HashSet<PlayerKey>();
        for (int i = 0; i < members.Count; i++)
            if (!seen.Add(members[i])) return EnqueueResult.AlreadyQueued;

        // Prefer the inviter's existing GroupId from GroupRegistry over a fresh one.
        groupId = existingGroup
            ?? _groups.GroupOf(members[0])
            ?? GroupId.New();

        // Initiator attribution is only emitted for open parties: closed-party members already
        // know the leader is the one who queues, so the bare "Queued for ..." reply is correct.
        bool isOpenParty = _groups.ModeOf(groupId) == GroupMode.Open;

        for (int i = 0; i < members.Count; i++)
        {
            var p = members[i];
            var rating = _ratings.Get(p, def.GameType);
            _matcher.Enqueue(p, rating, queueName, groupId);
            PlayerKey? attribution =
                (isOpenParty && initiator is PlayerKey ini && !p.Equals(ini)) ? initiator : null;
            _telemetry.OnQueueAdded(p, queueName, at, attribution);
        }
        return EnqueueResult.Ok;
    }

    /// <summary>
    /// Cancels a Forming match because one or more players failed the staging-phase readiness
    /// check. Every player in <paramref name="afkPlayers"/> is marked as an abandoner regardless
    /// of whether they reached Active -- showing up to the arena and then going idle counts the
    /// same as never showing up. The match transitions to Cancelled and the AFK players are
    /// assessed a <see cref="PenaltyKind.StagingAfk"/> penalty (a milder ladder than the
    /// in-match <see cref="PenaltyKind.Abandonment"/> kind, since the match never started).
    /// </summary>
    public bool CancelMatchAsAfk(Guid matchId, IReadOnlyList<PlayerKey> afkPlayers, DateTimeOffset at)
    {
        if (!_matches.TryGetValue(matchId, out var match)) return false;
        if (match.State != MatchState.Forming) return false;

        // Snapshot the queue and the readied (= rostered minus AFK) set before FinalizeMatch
        // tears down _matchQueue and clears _matchOf. We re-enqueue these players AFTER
        // finalization so the eligibility check sees them as no-longer-in-match.
        var queueDef = _matchQueue.TryGetValue(matchId, out var qd) ? qd : null;
        var afkSet = new HashSet<PlayerKey>(afkPlayers);
        var readied = new List<PlayerKey>();
        for (int t = 0; t < match.Teams.Count; t++)
            for (int j = 0; j < match.Teams[t].Count; j++)
            {
                var p = match.Teams[t][j];
                if (!afkSet.Contains(p)) readied.Add(p);
            }

        match.CancelAsAfk(afkPlayers, at);
        FinalizeMatch(match, at);

        if (queueDef is not null)
            ReQueueReadiedAtFront(queueDef, readied, at);

        return true;
    }

    /// <summary>
    /// Cancels a still-Forming match immediately -- the orchestrator calls this at GO! when a
    /// rostered player isn't on their ship (e.g. specced during the countdown and didn't return).
    /// Runs the same finalization the join-timeout would, just now: no-shows are flagged and
    /// abandonment is assessed by the candidate rule (a lone leaver who stranded no teammate stays
    /// penalty-free), then the match is torn down. Unlike <see cref="CancelMatchAsAfk"/> it neither
    /// force-flags the absentee nor re-queues anyone -- it is purely "do the join-timeout cancel
    /// now." Returns false if the match is unknown or already past Forming.
    /// </summary>
    public bool CancelForming(Guid matchId, DateTimeOffset at)
    {
        if (!_matches.TryGetValue(matchId, out var match)) return false;
        if (match.State != MatchState.Forming) return false;
        match.Cancel(at);
        FinalizeMatch(match, at);
        return true;
    }

    /// <summary>
    /// Auto-re-enqueue players who readied during a match that was cancelled because of AFK
    /// participants. Mirrors <see cref="ApplyKothReenqueue"/>'s pattern: priority insertion
    /// at the head of the queue, group affiliation preserved via <see cref="GroupRegistry.GroupOf"/>,
    /// and a standard <see cref="IMatchmakingTelemetry.OnQueueAdded"/> per re-add. Skips
    /// players who became ineligible (rare -- e.g. a stale penalty that outlived the match).
    /// </summary>
    private void ReQueueReadiedAtFront(QueueDefinition queue, IReadOnlyList<PlayerKey> readied, DateTimeOffset at)
    {
        for (int i = 0; i < readied.Count; i++)
        {
            var p = readied[i];
            if (CheckEligibility(p).Status != EligibilityStatus.Available) continue;
            var rating = _ratings.Get(p, queue.GameType);
            var groupId = _groups.GroupOf(p);
            if (_matcher.EnqueuePriority(p, rating, queue.UniqueId, groupId))
                _telemetry.OnQueueAdded(p, queue.UniqueId, at);
        }
    }

    /// <summary>Removes a player from <paramref name="queueName"/> if they were enqueued.</summary>
    public bool Dequeue(PlayerKey player, string queueName, DateTimeOffset at,
        QueueRemovalReason reason = QueueRemovalReason.Cancel)
    {
        if (!_matcher.Dequeue(player, queueName)) return false;
        _telemetry.OnQueueRemoved(player, queueName, at, reason);
        return true;
    }

    /// <summary>
    /// Relays a player's request (via <c>?connect discord</c>) to link their in-game name to a
    /// Discord alias. The engine is identity-agnostic: it stores nothing and only fires
    /// <see cref="IMatchmakingTelemetry.OnDiscordLinkRequested"/> so the event-stream adapter can
    /// forward the request to the external service that owns account linking and opt-in. Trims
    /// the alias and returns <see langword="false"/> (emitting nothing) when it's blank.
    /// </summary>
    public bool RequestDiscordLink(PlayerKey player, string discordAlias, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(discordAlias)) return false;
        _telemetry.OnDiscordLinkRequested(player, discordAlias.Trim(), at);
        return true;
    }

    /// <summary>Removes a player from every queue they are searching in.</summary>
    public IReadOnlyList<string> DequeueEverywhere(PlayerKey player, DateTimeOffset at,
        QueueRemovalReason reason = QueueRemovalReason.Cancel)
    {
        var names = _matcher.DequeueEverywhere(player);
        for (int i = 0; i < names.Count; i++)
            _telemetry.OnQueueRemoved(player, names[i], at, reason);
        return names;
    }

    /// <summary>
    /// Operator-driven full wipe of <paramref name="player"/>'s matchmaking state. Used by the
    /// <c>?clashreset</c> command. Clears every persistent and in-flight artifact tied to the
    /// player: queue entries, party membership, any in-progress match (without re-applying an
    /// abandonment penalty to the target), penalty history, KOTH consecutive-defense counters,
    /// pending griefing penalties targeting them, and -- unless <paramref name="keepRating"/>
    /// is set -- their rating rows for every game type. Returns a <see cref="ResetSummary"/>
    /// describing what was actually changed so the caller can render a concise reply.
    /// </summary>
    /// <remarks>
    /// For an in-flight match, the player is treated as if they left the arena (the match's
    /// usual grace / team-collapse path runs normally for the remaining participants) but is
    /// then excluded from the post-match abandonment-candidate set so <c>FinalizeMatch</c>
    /// does not reapply a penalty that we just wiped. The match is not cancelled for the other
    /// participants -- they continue, and the absent reset target counts as missing from their
    /// team. The reset target's <c>_matchOf</c> entry is dropped so they can immediately queue
    /// elsewhere if reconnected.
    /// </remarks>
    public ResetSummary ResetPlayer(PlayerKey player, DateTimeOffset at, bool keepRating)
    {
        if (player.IsDefault) throw new ArgumentException("Player must not be default.", nameof(player));

        // Snapshot counts that we'll surface in the reply BEFORE we mutate state.
        int penaltyEventsCleared = 0;
        foreach (var r in _penalties.Snapshot())
            if (r.Player.Equals(player)) penaltyEventsCleared++;

        int ratingsCleared = 0;
        if (!keepRating)
        {
            foreach (var e in _ratings.Snapshot())
                if (e.Player.Equals(player)) ratingsCleared++;
        }

        // Active match: route the departure through the normal OnPlayerLeft path so team-collapse
        // bookkeeping fires for the other participants, then exclude the target from the
        // abandonment-candidate set so the eventual FinalizeMatch does not re-penalize them.
        bool removedFromMatch = false;
        if (_matchOf.TryGetValue(player, out var matchId) && _matches.TryGetValue(matchId, out var match))
        {
            var prev = SnapshotCollapsed(match);
            match.OnPlayerLeft(player, at);
            DiffCollapsedAndEmit(match, prev);
            match.ExcludeFromAbandonment(player);
            _matchOf.Remove(player);
            removedFromMatch = true;
        }

        // Dequeue from every queue (no-op if they weren't queued -- counts what was actually
        // removed for the caller's reply). LeaveGroup below also sweeps queues for surviving
        // party members, but the target themselves is already gone after this call.
        var queueNames = _matcher.DequeueEverywhere(player);
        for (int i = 0; i < queueNames.Count; i++)
            _telemetry.OnQueueRemoved(player, queueNames[i], at, QueueRemovalReason.Reset);
        int removedFromQueues = queueNames.Count;

        bool leftGroup = LeaveGroup(player, at);

        // KOTH consecutive-defense counters: drop every (player, queueName) entry.
        List<(PlayerKey, string)>? cdToRemove = null;
        foreach (var k in _consecutiveDefenses.Keys)
            if (k.Player.Equals(player)) (cdToRemove ??= new List<(PlayerKey, string)>()).Add(k);
        if (cdToRemove is not null)
            foreach (var k in cdToRemove) _consecutiveDefenses.Remove(k);

        // Pending griefing penalties: drop every entry where the reset target is the *target*
        // of the penalty. For remaining entries, strip the reset target out of EligibleVoters /
        // VotesReceived (a wiped player should not contribute to the N-of-M veto threshold).
        // Collect keys first; we mutate _pendingGriefs after the iteration completes.
        List<(Guid, PlayerKey)>? pgTargetRemove = null;
        List<(Guid, PlayerKey)>? pgVoterTrim = null;
        foreach (var kvp in _pendingGriefs)
        {
            if (kvp.Key.Target.Equals(player))
                (pgTargetRemove ??= new List<(Guid, PlayerKey)>()).Add(kvp.Key);
            else if (kvp.Value.EligibleVoters.Contains(player) || kvp.Value.VotesReceived.Contains(player))
                (pgVoterTrim ??= new List<(Guid, PlayerKey)>()).Add(kvp.Key);
        }
        int pendingGriefsCleared = 0;
        if (pgTargetRemove is not null)
        {
            foreach (var k in pgTargetRemove)
            {
                _pendingGriefs.Remove(k);
                pendingGriefsCleared++;
            }
        }
        if (pgVoterTrim is not null)
        {
            foreach (var k in pgVoterTrim)
            {
                var pending = _pendingGriefs[k];
                var newEligible = new HashSet<PlayerKey>(pending.EligibleVoters);
                newEligible.Remove(player);
                var newVotes = new HashSet<PlayerKey>(pending.VotesReceived);
                newVotes.Remove(player);
                _pendingGriefs[k] = pending with { EligibleVoters = newEligible, VotesReceived = newVotes };
            }
        }

        // Penalty history: full wipe across every kind for this player.
        _penalties.ReplacePlayerHistory(player, Array.Empty<PenaltyRecord>());

        // Ratings: optional wipe. Iterate the snapshot rather than holding the store's gate.
        if (!keepRating && ratingsCleared > 0)
        {
            foreach (var entry in _ratings.Snapshot())
            {
                if (entry.Player.Equals(player))
                    _ratings.Remove(entry.Player, entry.GameType);
            }
        }

        return new ResetSummary(
            RemovedFromQueues: removedFromQueues,
            LeftGroup: leftGroup,
            RemovedFromMatch: removedFromMatch,
            PenaltyEventsCleared: penaltyEventsCleared,
            RatingsCleared: ratingsCleared,
            PendingGriefsCleared: pendingGriefsCleared);
    }

    /// <summary>
    /// Drives time-based transitions: ticks every active match (which may finalize), then tries
    /// to propose new matches from the queues.
    /// </summary>
    public void Tick(DateTimeOffset at)
    {
        List<ActiveMatch>? toFinalize = null;
        List<(ActiveMatch Match, Dictionary<int, DateTimeOffset> Prev, HashSet<PlayerKey> PrevAbandoned)>? collapseSnapshots = null;
        foreach (var m in _matches.Values)
        {
            // Snapshot collapse map and the already-abandoned set before Tick so we can emit
            // collapse/recovery and free-to-leave events for the transitions this tick produces.
            (collapseSnapshots ??= new()).Add((m, SnapshotCollapsed(m), SnapshotAbandoned(m)));
            m.Tick(at);
            if (HasFinished(m)) (toFinalize ??= new List<ActiveMatch>()).Add(m);
        }
        if (collapseSnapshots is not null)
            foreach (var (m, prev, prevAbandoned) in collapseSnapshots)
                if (!HasFinished(m))
                {
                    DiffCollapsedAndEmit(m, prev);
                    EmitFreeToLeaveTransitions(m, prevAbandoned, at);
                }
        if (toFinalize is not null)
            foreach (var m in toFinalize) FinalizeMatch(m, at);

        ExpireVetoWindows(at);
        var expiredInvites = _groups.PruneExpiredInvitationsAndReport(at);
        for (int i = 0; i < expiredInvites.Count; i++)
        {
            var invite = expiredInvites[i];
            _telemetry.OnInviteExpired(invite.Inviter, invite.Invitee, at);
        }

        while (true)
        {
            var proposal = _matcher.TryProposeMatch();
            if (proposal is null) break;
            FormMatchFromProposal(proposal, at);
        }

        // Run after proposal popping so a queue whose 7+1 just got formed into a match doesn't
        // briefly trip the near-full event on its way back to empty.
        SweepNearFullThresholds();

        // Likewise run the AFK dwell sweep after proposal popping so a player who was just matched
        // this tick (already dequeued above) is never warned or culled on their way into a match.
        SweepAfkDwell(at);
    }

    /// <summary>
    /// Warns once and then auto-culls players who have sat in a queue past its configured AFK
    /// dwell thresholds (<see cref="QueueDefinition.AfkDwellWarning"/> /
    /// <see cref="QueueDefinition.AfkDwellCull"/>). The clock is the entry's
    /// <see cref="Queue.QueueEntry.LastSeenAt"/>, so re-queuing OR a present player re-issuing
    /// <c>?play</c> (which touches LastSeenAt) resets the timer and re-arms the warning. No-op for
    /// queues with warning disabled (null / non-positive).
    /// </summary>
    private void SweepAfkDwell(DateTimeOffset at)
    {
        // Prune the warned-set of players who are no longer in their queue (culled, cancelled,
        // disconnected, matched, ...) to bound memory. Correctness of re-warning comes from the
        // EnqueuedAt epoch comparison below, not from this prune.
        if (_dwellWarned.Count > 0)
        {
            List<(PlayerKey, string)>? stale = null;
            foreach (var kvp in _dwellWarned)
            {
                var key = kvp.Key;
                if (_queues.TryGet(key.Queue, out var d) && d.Queue.Contains(key.Player)) continue;
                (stale ??= new List<(PlayerKey, string)>()).Add(key);
            }
            if (stale is not null)
                foreach (var key in stale) _dwellWarned.Remove(key);
        }

        List<(PlayerKey Player, string Queue)>? toCull = null;
        foreach (var def in _queues.Definitions)
        {
            var warn = def.AfkDwellWarning;
            if (warn is not { } warnSpan || warnSpan <= TimeSpan.Zero) continue;  // disabled
            var cull = def.AfkDwellCull;

            var snapshot = def.Queue.Snapshot();
            for (int i = 0; i < snapshot.Count; i++)
            {
                var entry = snapshot[i];
                var dwell = at - entry.LastSeenAt;

                if (cull is { } cullSpan && cullSpan > TimeSpan.Zero && dwell >= cullSpan)
                {
                    (toCull ??= new List<(PlayerKey, string)>()).Add((entry.Player, def.UniqueId));
                    continue;
                }

                if (dwell >= warnSpan)
                {
                    var key = (entry.Player, def.UniqueId);
                    // Fire once per (player, liveness-epoch): a fresh LastSeenAt re-warns.
                    if (!_dwellWarned.TryGetValue(key, out var warnedFor) || warnedFor != entry.LastSeenAt)
                    {
                        _dwellWarned[key] = entry.LastSeenAt;
                        _telemetry.OnQueueDwellWarning(entry.Player, def.UniqueId, at, dwell);
                    }
                }
            }
        }

        if (toCull is not null)
        {
            foreach (var (player, queueName) in toCull)
            {
                if (_matcher.Dequeue(player, queueName))
                    _telemetry.OnQueueRemoved(player, queueName, at, QueueRemovalReason.AfkCull);
                _dwellWarned.Remove((player, queueName));
            }
        }
    }

    /// <summary>
    /// Fires <see cref="IMatchmakingTelemetry.OnQueueNearFull"/> the first tick a queue reaches
    /// <c>TotalPlayers - 1</c> waiters and re-arms when its count drops back below the threshold.
    /// Skipped entirely for queues with fewer than <see cref="NearFullMinShape"/> total slots.
    /// </summary>
    private void SweepNearFullThresholds()
    {
        foreach (var def in _queues.Definitions)
        {
            if (def.Shape.TotalPlayers < NearFullMinShape) continue;
            int threshold = def.Shape.TotalPlayers - 1;
            int count = def.Queue.Count;

            if (count >= threshold)
            {
                if (_nearFullFired.Add(def.UniqueId))
                {
                    var snapshot = def.Queue.Snapshot();
                    var waiting = new PlayerKey[snapshot.Count];
                    for (int i = 0; i < snapshot.Count; i++) waiting[i] = snapshot[i].Player;
                    _telemetry.OnQueueNearFull(def.UniqueId, waiting, count, def.Shape.TotalPlayers);
                }
            }
            else
            {
                _nearFullFired.Remove(def.UniqueId);
            }
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
            _telemetry.OnGriefingConfirmed(pending, _penalties.TimeoutUntil(pending.Target) ?? at);
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
            _telemetry.OnGriefingConfirmed(pending, _penalties.TimeoutUntil(pending.Target) ?? at);
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
            teamCollapseGrace: def.TeamCollapseGrace,
            eliminationCooldown: def.EliminationCooldown);
        _matches[matchId] = match;
        _matchQueue[matchId] = def;

        // The matcher already dequeued these players from every queue they were searching; surface
        // a removal (reason=Matched) for the queue this match formed from AND for any other queues
        // they were searching (carried on proposal.AlsoRemovedFrom) so the event stream's queue board
        // reflects every drop, not just the one on the queue this match formed from.
        for (int t = 0; t < proposal.Teams.Count; t++)
            for (int j = 0; j < proposal.Teams[t].Count; j++)
            {
                var p = proposal.Teams[t][j];
                _matchOf[p] = matchId;
                _telemetry.OnQueueRemoved(p, proposal.QueueName, at, QueueRemovalReason.Matched);
                if (proposal.AlsoRemovedFrom.TryGetValue(p, out var otherQueues))
                    for (int q = 0; q < otherQueues.Count; q++)
                        _telemetry.OnQueueRemoved(p, otherQueues[q], at, QueueRemovalReason.Matched);
            }

        _telemetry.OnMatchProposed(proposal);
    }

    private void FinalizeMatch(ActiveMatch m, DateTimeOffset at)
    {
        if (m.Outcome is null) return;

        double weight = _matchQueue.TryGetValue(m.MatchId, out var queueDef) ? queueDef.RatingWeight : 1.0;

        if (m.Outcome.FinalState != MatchState.Cancelled)
            _ratingUpdater.ApplyOutcome(_ratings, m.Outcome, at, weight);

        // Cancelled matches use the milder StagingAfk ladder (the match never actually started,
        // so nobody else's match was ruined). Live abandons use the standard Abandonment ladder.
        // Falls back to Abandonment if StagingAfk wasn't registered, so test rigs that only
        // register the legacy policies still work.
        var abandonKind = m.Outcome.FinalState == MatchState.Cancelled && _penalties.HasPolicy(PenaltyKind.StagingAfk)
            ? PenaltyKind.StagingAfk
            : PenaltyKind.Abandonment;
        for (int i = 0; i < m.Outcome.AbandonedBy.Count; i++)
        {
            var p = m.Outcome.AbandonedBy[i];
            int count = _penalties.RecordPenalty(p, abandonKind, at);
            var until = _penalties.TimeoutUntil(p)!.Value;
            // A staging-AFK violation auto-disables the player's auto-queue preference: leaving it
            // on would keep dragging an away-from-keyboard player into matches they'll just AFK
            // again. Only act (and notify) when it was actually on.
            if (abandonKind == PenaltyKind.StagingAfk && _autoQueue.IsEnabled(p))
            {
                _autoQueue.Set(p, false);
                _telemetry.OnAutoQueueDisabledByAfk(p, at);
            }
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

        // Per-player auto-queue (?autoqueue on): re-enqueue opted-in participants of a match that
        // actually ran back into the queue it formed from. Runs after KOTH so a winner who also has
        // auto-queue on is already queued (priority) and won't be added a second time. Cancelled
        // matches are excluded -- their readied players are handled by ReQueueReadiedAtFront, and an
        // AFK violator already had auto-queue switched off in the abandon loop above.
        if (queueDef is not null && m.Outcome.FinalState != MatchState.Cancelled)
            ApplyAutoQueueReenqueue(queueDef, m, at);

        _telemetry.OnMatchEnded(m.Outcome);
    }

    /// <summary>
    /// Re-enqueues every match participant who has the <c>?autoqueue</c> preference enabled back
    /// into the queue the match formed from. Mirrors <see cref="ApplyKothReenqueue"/>'s eligibility
    /// gating (connected, not in timeout, not already in another match) but adds players at the
    /// back via <see cref="Matcher.Enqueue"/> with group affiliation preserved. Players who
    /// abandoned the match are skipped -- they bailed, so dragging them back in (and, for live
    /// abandons, on top of a fresh queue-lock) would be wrong. A player already in the queue (e.g.
    /// a KOTH winner promoted just above) is left as-is and fires no duplicate notice.
    /// </summary>
    private void ApplyAutoQueueReenqueue(QueueDefinition queue, ActiveMatch m, DateTimeOffset at)
    {
        var abandoned = m.Outcome!.AbandonedBy.Count > 0
            ? new HashSet<PlayerKey>(m.Outcome.AbandonedBy)
            : null;

        for (int t = 0; t < m.Teams.Count; t++)
        {
            for (int j = 0; j < m.Teams[t].Count; j++)
            {
                var p = m.Teams[t][j];
                if (abandoned is not null && abandoned.Contains(p)) continue;
                if (!_autoQueue.IsEnabled(p)) continue;
                if (!_connected.Contains(p)) continue;            // gone -- nothing to re-queue
                if (IsInActiveMatch(p)) continue;                 // already pulled into another match
                if (CheckEligibility(p).Status == EligibilityStatus.InTimeout) continue;

                var rating = _ratings.Get(p, queue.GameType);
                var groupId = _groups.GroupOf(p);
                if (_matcher.Enqueue(p, rating, queue.UniqueId, groupId))
                    _telemetry.OnAutoQueued(p, queue.UniqueId, at);
            }
        }
    }

    private void ApplyKothReenqueue(QueueDefinition queue, MatchOutcome outcome, DateTimeOffset at)
    {
        var winners = outcome.RankedTeams[0].Players;
        var winningSet = new HashSet<PlayerKey>(winners);

        // Reset losers' defense counters.
        for (int r = 1; r < outcome.RankedTeams.Count; r++)
        {
            foreach (var p in outcome.RankedTeams[r].Players)
                _consecutiveDefenses.Remove((p, queue.UniqueId));
        }

        // Determine whether winners exceed the cap.
        bool atLeastOneAtCap = false;
        foreach (var p in winners)
        {
            int prior = _consecutiveDefenses.TryGetValue((p, queue.UniqueId), out var c) ? c : 0;
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

            int defensesUsed;
            if (atLeastOneAtCap)
            {
                _consecutiveDefenses.Remove((p, queue.UniqueId));
                _matcher.Enqueue(p, rating, queue.UniqueId, groupId);
                defensesUsed = 0;
            }
            else
            {
                int prior = _consecutiveDefenses.TryGetValue((p, queue.UniqueId), out var c) ? c : 0;
                _consecutiveDefenses[(p, queue.UniqueId)] = prior + 1;
                _matcher.EnqueuePriority(p, rating, queue.UniqueId, groupId);
                defensesUsed = prior + 1;
            }
            _telemetry.OnWinnerPromoted(
                p, queue.UniqueId, at, defensesUsed, queue.MaxConsecutiveDefenses, atLeastOneAtCap);
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

            // Skip the veto window if there aren't enough eligible voters to ever rescind. The
            // penalty is final immediately -- fire OnGriefingConfirmed so the adapter can DM the
            // target. (We don't fire OnGriefingFlagged here, since there's no veto period to
            // tell teammates about.)
            if (eligibleVoters.Count < def.VetoesRequired)
            {
                var confirmed = new PendingGriefingPenalty(
                    MatchId: m.MatchId,
                    Target: flag.Player,
                    Reason: flag.Reason,
                    PenaltyAppliedAt: at,
                    VetoWindowEndsAt: at,                    // already-closed window
                    VetoesRequired: def.VetoesRequired,
                    EligibleVoters: eligibleVoters,
                    VotesReceived: new HashSet<PlayerKey>());
                _telemetry.OnGriefingConfirmed(confirmed, _penalties.TimeoutUntil(flag.Player) ?? at);
                continue;
            }

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
            _telemetry.OnGriefingFlagged(pending, _penalties.TimeoutUntil(flag.Player) ?? at);
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

    /// <summary>Captures the set of players already Abandoned so a follow-up call to
    /// <see cref="EmitFreeToLeaveTransitions"/> can tell which abandons are fresh this tick.</summary>
    private static HashSet<PlayerKey> SnapshotAbandoned(ActiveMatch m)
    {
        var into = new HashSet<PlayerKey>();
        foreach (var kvp in m.Statuses)
            if (kvp.Value == PlayerStatus.Abandoned)
                into.Add(kvp.Key);
        return into;
    }

    /// <summary>
    /// After a tick, notify the still-active teammates of any player whose grace window just
    /// expired (a fresh transition into <see cref="PlayerStatus.Abandoned"/>) that they may now
    /// leave penalty-free. Only while the match is still live -- if it ended this tick, the
    /// end-of-match messaging covers the survivors instead.
    /// </summary>
    private void EmitFreeToLeaveTransitions(ActiveMatch m, HashSet<PlayerKey> prevAbandoned, DateTimeOffset now)
    {
        if (m.State != MatchState.Live) return;

        foreach (var kvp in m.Statuses)
        {
            if (kvp.Value != PlayerStatus.Abandoned) continue;
            var abandoner = kvp.Key;
            if (prevAbandoned.Contains(abandoner)) continue;        // not a fresh transition
            if (!m.IsCandidateAbandoner(abandoner)) continue;       // lives-out leaver, not an abandon

            var survivors = m.ActiveTeammatesOf(abandoner);
            if (survivors.Count == 0) continue;
            _telemetry.OnTeammateAbandoned(survivors, abandoner, m.MatchId, now);
        }
    }
}

/// <summary>What <see cref="MatchmakingEngine.ResetPlayer"/> actually changed. Each field is
/// zero / false when there was nothing to clear, so callers can render a "no persistent data
/// found" reply by checking whether the whole record is empty.</summary>
public readonly record struct ResetSummary(
    int RemovedFromQueues,
    bool LeftGroup,
    bool RemovedFromMatch,
    int PenaltyEventsCleared,
    int RatingsCleared,
    int PendingGriefsCleared);

/// <summary>Outcome of a <see cref="MatchmakingEngine.TryEnqueue"/> call.</summary>
public enum EnqueueResult
{
    Ok,
    UnknownQueue,
    NotConnected,
    InMatch,
    InTimeout,
    AlreadyQueued,

    /// <summary>The player (or party) was already in the requested queue, and the repeat
    /// <c>?play</c> was treated as a liveness ping: their AFK dwell clock was refreshed without
    /// changing queue position. Distinct from <see cref="AlreadyQueued"/> so the reply can confirm
    /// the refresh rather than read as an inert "already queued".</summary>
    AlreadyQueuedRefreshed,

    /// <summary>The group has more members than the queue's <c>PlayersPerTeam</c>.</summary>
    GroupTooLarge,
}
