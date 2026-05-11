using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;

namespace ClashEngine.Orchestration;

/// <summary>
/// Conducts a single match physically: places players in the configured match arena, sets ship
/// and freq, warps to per-team spawn, runs an idle-detection staging phase, then a countdown,
/// and returns players to spec on completion. One instance per active match.
/// </summary>
public sealed class MatchOrchestrator
{
    private const string LogCategory = nameof(MatchOrchestrator);

    private readonly Guid _matchId;
    private readonly QueueDefinition _queue;
    private readonly MatchProposal _proposal;
    private readonly MatchmakingEngine _engine;
    private readonly IGame _game;
    private readonly IChat _chat;
    private readonly IMainloopTimer _timer;
    private readonly IArenaManager _arenaManager;
    private readonly IClock _clock;
    private readonly ILogManager _log;
    private readonly PlayerKeyResolver _resolver;
    private readonly ClashLog _verbose;
    private readonly MatchAudience? _audience;
    private readonly MatchFreqAllocator? _freqAllocator;
    private readonly MatchStatsRegistry? _matchStats;
    private readonly IComponentBroker? _broker;

    /// <summary>Per-player ship the participant was on at the moment they last specced themselves
    /// out. Populated by <see cref="OnPlayerSpecced"/>; consumed by <see cref="TryReturn"/> so the
    /// returner re-enters in the exact ship they left, preserving any post-death ship-change they
    /// made within the grace window. Cleared on successful return.</summary>
    private readonly Dictionary<PlayerKey, ShipType> _shipAtLeave = new();

    /// <summary>Team-0 freq for this match; team-t uses <c>_freqBase + t * 100</c>. Defaults to
    /// the legacy 100/200/... convention until <see cref="BeginSetup"/> reserves a rotating base
    /// from <see cref="MatchFreqAllocator"/>. Read by <see cref="MatchFreqAdvisor"/> and the LVZ
    /// team adapter via the allocator so they all see the same numbers.</summary>
    private short _freqBase = MatchFreqAllocator.BaseFreq;

    /// <summary>Staging-phase idle detection (per-player).</summary>
    private readonly IdleStateTracker _idleTracker = new();

    /// <summary>Spawn pick + drift-back enforcement.</summary>
    private readonly SpawnDriftEnforcer _drift;

    /// <summary>RNG seam for spawn selection. Production uses
    /// <see cref="DefaultRandomSource.Instance"/>; tests pass a deterministic
    /// <see cref="IRandomSource"/> to assert spawn-selection behavior.</summary>
    private readonly IRandomSource _rng;

    /// <summary>Seconds remaining in the live countdown. Decrements on each <see cref="OnCountdownTick"/>.</summary>
    private int _countdownSecondsRemaining;

    /// <summary>Per-player placement that's still pending arena-entry. Populated for every
    /// participant in BeginSetup; the entry is removed once the player has been placed onto
    /// their assigned ship (either immediately if already in the right arena, or via
    /// <see cref="OnPlayerEnteredArena"/> after the SendToArena transfer completes).</summary>
    private readonly Dictionary<PlayerKey, PlacementInfo> _pendingPlacement = new();

    private readonly record struct PlacementInfo(ShipType Ship, short Freq, short SpawnX, short SpawnY);

    public MatchOrchestrator(
        Guid matchId,
        QueueDefinition queue,
        MatchProposal proposal,
        MatchmakingEngine engine,
        IGame game,
        IChat chat,
        IMainloopTimer timer,
        IArenaManager arenaManager,
        IClock clock,
        ILogManager log,
        PlayerKeyResolver resolver,
        ClashLog verbose,
        MatchAudience? audience = null,
        MatchFreqAllocator? freqAllocator = null,
        IRandomSource? rng = null,
        MatchStatsRegistry? matchStats = null,
        IComponentBroker? broker = null)
    {
        _matchId = matchId;
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _verbose = verbose ?? throw new ArgumentNullException(nameof(verbose));
        _audience = audience;
        _freqAllocator = freqAllocator;
        _rng = rng ?? DefaultRandomSource.Instance;
        _matchStats = matchStats;
        _broker = broker;
        _drift = new SpawnDriftEnforcer(_queue, _proposal);

        for (int t = 0; t < _proposal.Teams.Count; t++)
            for (int j = 0; j < _proposal.Teams[t].Count; j++)
                _idleTracker.RegisterParticipant(_proposal.Teams[t][j]);
    }

    /// <summary>True iff <paramref name="player"/> is one of this match's participants. Used
    /// by the orchestrator registry to route per-event callbacks to the owning match.</summary>
    public bool OwnsPlayer(PlayerKey player)
    {
        for (int t = 0; t < _proposal.Teams.Count; t++)
            for (int j = 0; j < _proposal.Teams[t].Count; j++)
                if (_proposal.Teams[t][j] == player) return true;
        return false;
    }

    public Guid MatchId => _matchId;
    public MatchPhase Phase { get; private set; } = MatchPhase.Setup;

    /// <summary>Single funnel for phase transitions so every change shows up in the log when
    /// verbose. Returns the new phase for ergonomic chaining.</summary>
    private MatchPhase SetPhase(MatchPhase next)
    {
        var prev = Phase;
        Phase = next;
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory, $"Match {_matchId:N} phase {prev} -> {next}");
        return next;
    }

    /// <summary>
    /// Place every matched player: warp to match arena (or in-place), set ship+freq, lock,
    /// then enter the staging window during which we detect AFK players via position packets.
    /// </summary>
    public void BeginSetup()
    {
        string? arenaName = string.IsNullOrEmpty(_queue.MatchArenaName) ? null : _queue.MatchArenaName;

        _drift.ChooseSpawnForEachTeam(_proposal, _rng);

        // Reserve a rotating freq base for this match so concurrent matches in the same arena
        // don't all stack their teams on freqs 100/200. Falls back to the static convention when
        // no allocator was injected (e.g. test paths constructing the orchestrator directly).
        _freqBase = _freqAllocator?.Allocate(_matchId, arenaName, _proposal.Teams.Count)
            ?? MatchFreqAllocator.BaseFreq;

        for (int t = 0; t < _proposal.Teams.Count; t++)
        {
            short freq = (short)(_freqBase + t * MatchFreqAllocator.FreqStep);
            var spawn = _drift.ChosenSpawn(t);
            for (int j = 0; j < _proposal.Teams[t].Count; j++)
            {
                var key = _proposal.Teams[t][j];
                var player = _resolver.Resolve(key);
                if (player is null)
                {
                    _log.LogM(LogLevel.Warn, LogCategory,
                        $"Match {_matchId:N}: cannot resolve {key.Name} for setup.");
                    continue;
                }

                var ship = ShipFor(t, j);
                _pendingPlacement[key] = new PlacementInfo(ship, freq, spawn.X, spawn.Y);

                if (arenaName is not null && !IsInArena(player, arenaName))
                {
                    // Different arena (or no arena yet): transfer asynchronously. The placement
                    // (ship + freq + warp + lock) finishes when EnterArena fires for them, via
                    // the registry's PlayerActionCallback dispatcher -> OnPlayerEnteredArena.
                    _arenaManager.SendToArena(player, arenaName, spawn.X, spawn.Y);
                    if (_verbose.IsDebug)
                        _verbose.Debug(LogCategory,
                            $"Match {_matchId:N}: sending {key.Name} to arena '{arenaName}'; placement deferred.");
                }
                else
                {
                    // Already in target arena (or no arena configured): place now.
                    PlacePlayerOnShip(key, player);
                }
            }
        }

        SetPhase(MatchPhase.Staging);
        // One-shot timer: SS mainloop rejects interval=0 (must be > 0 or Timeout.Infinite).
        _timer.SetTimer(OnStagingEnd, (int)_queue.StagingDuration.TotalMilliseconds, Timeout.Infinite, this);

        // Mirror SS.Matchmaking.TeamVersusMatch: DM the "you've been placed" notice to each
        // participant as a private message FROM THEMSELVES so it lands in their personal chat
        // window (Continuum renders it as "(theirname)>...") and is visually impossible to miss.
        // Spectators get a plain arena broadcast since they don't need the call-to-action.
        var notice =
            $"Match found! Move or fire within {(int)_queue.StagingDuration.TotalSeconds} seconds to confirm. " +
            "Change ships freely until just before the match starts. Spec to abandon.";
        SendDmToParticipants(notice);
        BroadcastToSpectators(notice);
    }

    /// <summary>
    /// Called by <see cref="MatchOrchestratorRegistry"/> when one of this match's participants
    /// enters an arena (<c>PlayerActionCallback</c> with <c>EnterArena</c>). If they entered the
    /// configured match arena AND the orchestrator still has a pending placement for them, this
    /// finishes the setup: ship + freq + warp + lock.
    /// </summary>
    public void OnPlayerEnteredArena(PlayerKey key, Arena arena)
    {
        try
        {
            if (Phase is not (MatchPhase.Setup or MatchPhase.Staging or MatchPhase.Countdown))
                return;
            if (!_pendingPlacement.ContainsKey(key)) return;

            var arenaName = _queue.MatchArenaName;
            if (!string.IsNullOrEmpty(arenaName)
                && !string.Equals(arena.Name, arenaName, StringComparison.OrdinalIgnoreCase))
            {
                // Wrong arena -- player ended up somewhere else (e.g. ?go elsewhere). Drop the
                // pending placement; the engine's idle / join-timeout path will mark them AFK.
                _pendingPlacement.Remove(key);
                return;
            }

            var player = _resolver.Resolve(key);
            if (player is null) return;
            PlacePlayerOnShip(key, player);
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"Match {_matchId:N}: OnPlayerEnteredArena failed for {key.Name}: {ex}");
        }
    }

    private void PlacePlayerOnShip(PlayerKey key, Player player)
    {
        if (!_pendingPlacement.Remove(key, out var info)) return;

        _game.SetShipAndFreq(player, info.Ship, info.Freq);
        if (info.SpawnX != 0 || info.SpawnY != 0)
        {
            _game.WarpTo(player, info.SpawnX, info.SpawnY);
            // Anchor the idle tracker at the warp destination so stale pre-warp position
            // packets (in-flight when WarpTo went out) don't seed the tracker at the old
            // position and trigger a false-positive "moved" detection on the first post-warp
            // packet. SpawnX/Y are tile coords; position packets carry pixels (16 px/tile).
            _idleTracker.AnchorAt(key, (short)(info.SpawnX << 4), (short)(info.SpawnY << 4));
        }
        // No SS-Core IGame.Lock during setup -- the MatchFreqAdvisor enforces freq lock from
        // proposal time and opens a ship-change window for the staging duration so participants
        // can pick their loadout. Lock is re-enforced by the advisor for Countdown / Live.

        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"Match {_matchId:N}: placed {key.Name} on {info.Ship} freq {info.Freq} at ({info.SpawnX},{info.SpawnY}).");
    }

    private static bool IsInArena(Player player, string arenaName) =>
        player.Arena is { } a && string.Equals(a.Name, arenaName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Possible outcomes of a <see cref="TryReturn"/> request. <see cref="ReturnResult.Placed"/>
    /// is the success case; the others are diagnostic so the command handler can phrase the
    /// reply.
    /// </summary>
    public enum ReturnResult
    {
        Placed,
        NotInArena,
        AlreadyActive,
        MatchEnded,
        KnockedOut,
        UnresolvedPlayer,
    }

    /// <summary>
    /// Re-place <paramref name="key"/> onto their assigned ship + team freq, bypassing the
    /// FreqManager advisor (direct <c>SetShipAndFreq</c>). Used by the <c>?return</c> command to
    /// recover a participant who specced themselves out and would otherwise be blocked by
    /// <see cref="ClashEngine.Adapter.MatchFreqAdvisor"/> from getting back to their team's
    /// (private) freq. No-op for unknown players, players already on a ship, knocked-out players
    /// (lives exhausted), or once the match has reached Cleanup.
    /// </summary>
    public ReturnResult TryReturn(PlayerKey key)
    {
        if (Phase is MatchPhase.Cleanup) return ReturnResult.MatchEnded;
        if (!OwnsPlayer(key)) return ReturnResult.MatchEnded;
        if (!_engine.ActiveMatches.TryGetValue(_matchId, out var match)) return ReturnResult.MatchEnded;
        if (match.IsKnockedOut(key)) return ReturnResult.KnockedOut;

        var player = _resolver.Resolve(key);
        if (player is null) return ReturnResult.UnresolvedPlayer;

        var arenaName = _queue.MatchArenaName;
        if (!string.IsNullOrEmpty(arenaName) && !IsInArena(player, arenaName))
            return ReturnResult.NotInArena;

        if (player.Ship != ShipType.Spec)
            return ReturnResult.AlreadyActive;

        int teamIdx = -1, slotIdx = -1;
        for (int t = 0; t < _proposal.Teams.Count && teamIdx < 0; t++)
        {
            for (int j = 0; j < _proposal.Teams[t].Count; j++)
            {
                if (_proposal.Teams[t][j] == key) { teamIdx = t; slotIdx = j; break; }
            }
        }
        if (teamIdx < 0) return ReturnResult.MatchEnded;

        short freq = (short)(_freqBase + teamIdx * MatchFreqAllocator.FreqStep);
        // Prefer the ship the player was actually on at the moment they specced -- preserves any
        // post-death ship-change made within the grace window. Falls back to the queue's slotted
        // ship for first-time placements (no spec snapshot yet) or if the snapshot was lost.
        var ship = _shipAtLeave.TryGetValue(key, out var savedShip) && savedShip != ShipType.Spec
            ? savedShip
            : ShipFor(teamIdx, slotIdx);
        _game.SetShipAndFreq(player, ship, freq);

        // Apply the game type's items policy to the freshly-respawned loadout.
        ApplyReturnItemsAction(player, key);

        // Snapshot consumed; clear so the next leave-cycle isn't tainted by a stale value.
        _shipAtLeave.Remove(key);

        // Re-fire MatchAddPlayingCallback so SS.Matchmaking.MatchFocus rebuilds the returner's
        // PlayingInMatch state and re-attaches their current spectators to this match. The
        // initial firing happens at OnMatchStarted; subsequent ship<->spec transitions don't
        // re-fire it, so without this the IPlayerPositionAdvisor would drop other participants'
        // position packets to a returner whose PlayingInMatch was somehow cleared, and any
        // newly-attached spectators wouldn't be associated with this match.
        RefreshMatchFocus(key, player);

        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"Match {_matchId:N}: returned {key.Name} to {ship} freq {freq} " +
                $"(items={_queue.ReturnItemsAction}).");
        return ReturnResult.Placed;
    }

    /// <summary>
    /// Walk the broker's <see cref="IMatchFocusAdvisor"/> set to find the <see cref="IMatch"/>
    /// for this match (the LVZ adapter is the canonical advisor) and fire
    /// <see cref="MatchAddPlayingCallback"/> for <paramref name="player"/>. MatchFocus's
    /// <c>SetPlaying</c> is idempotent, so re-firing it on a player whose PlayingInMatch was
    /// already correct is a no-op; the value of the call is in the cases where the state had
    /// drifted (e.g. the player rotated through a different match's spectator focus while specced).
    /// No-op if the broker wasn't injected (test paths) or no advisor is registered.
    /// </summary>
    private void RefreshMatchFocus(PlayerKey key, Player player)
    {
        if (_broker is null) return;
        var advisors = _broker.GetAdvisors<IMatchFocusAdvisor>();
        foreach (var advisor in advisors)
        {
            var match = advisor.GetMatch(player);
            if (match is null) continue;
            MatchAddPlayingCallback.Fire(_broker, match, key.Name, player);
            return;
        }
    }

    /// <summary>
    /// Records that <paramref name="key"/> just specced themselves out of the match while still
    /// rostered, freezing a return-snapshot the next <see cref="TryReturn"/> can consume:
    /// <list type="bullet">
    /// <item>The ship they were on (so they re-enter in the same one).</item>
    /// <item>Their wire-authoritative item counts at this moment, captured into the recorder via
    /// <see cref="StatsRecorder.CaptureLastLeaveInventory"/>.</item>
    /// </list>
    /// Routed by <see cref="MatchOrchestratorRegistry"/> on every ship->Spec transition for an
    /// in-match participant. No-op for players we no longer own (e.g. already eliminated).
    /// </summary>
    public void OnPlayerSpecced(PlayerKey key, ShipType prevShip)
    {
        if (!OwnsPlayer(key)) return;
        if (prevShip == ShipType.Spec) return;
        _shipAtLeave[key] = prevShip;
        if (_matchStats is not null
            && _matchStats.ActiveRecorders.TryGetValue(_matchId, out var recorder))
        {
            recorder.CaptureLastLeaveInventory(key);
        }
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory, $"Match {_matchId:N}: {key.Name} specced from {prevShip}; return-snapshot saved.");
    }

    /// <summary>
    /// Applies the game type's <see cref="QueueDefinition.ReturnItemsAction"/> to the just-placed
    /// <paramref name="player"/> right after <see cref="IGame.SetShipAndFreq"/>. The fresh ship
    /// spawns with Continuum's full initial loadout; this method may zero them out (Burn) or
    /// reconcile them to the player's last in-match counts (Restore). <see cref="ItemsAction.Full"/>
    /// is a no-op.
    /// </summary>
    /// <remarks>
    /// Iterates the UNION of the ship's initial-inventory keys (what Continuum just handed the
    /// player) and the saved-leave keys (what they had at the moment of self-spec, possibly
    /// including items they picked up mid-match via greens that aren't in the slot's initial
    /// loadout). Each item is emitted as a single positive- or negative-prize call so Restore
    /// can ADD items the player accumulated past the initial (e.g. green-pickup thors on a ship
    /// whose initial Thor count is 0) and Burn zeroes everything they were carrying.
    /// </remarks>
    private void ApplyReturnItemsAction(Player player, PlayerKey key)
    {
        if (_queue.ReturnItemsAction == ItemsAction.Full) return;

        if (_matchStats is null
            || !_matchStats.ActiveRecorders.TryGetValue(_matchId, out var recorder))
            return;

        if (!recorder.TryGetInitialInventory(key, out var initial) || initial is null)
            return;

        IReadOnlyDictionary<ItemKind, int>? saved = null;
        if (_queue.ReturnItemsAction == ItemsAction.Restore)
            recorder.TryGetLastLeaveInventory(key, out saved);

        var items = new HashSet<ItemKind>(initial.Keys);
        if (saved is not null)
            foreach (var item in saved.Keys) items.Add(item);

        foreach (var item in items)
        {
            int current = initial.TryGetValue(item, out var c) ? c : 0;
            int target = (saved is not null && saved.TryGetValue(item, out var s)) ? s : 0;
            if (current == target) continue;
            var prize = PrizeForItem(item);
            if (prize is null) continue;
            int delta = target - current;
            // Positive prize value adds; negative removes. SS Core's GivePrize takes a count
            // separately from the prize id, but Continuum keys off the sign of the prize id.
            short prizeId = delta > 0 ? (short)prize.Value : (short)(-(short)prize.Value);
            _game.GivePrize(player, (Prize)prizeId, (short)Math.Abs(delta));
        }
    }

    private static Prize? PrizeForItem(ItemKind item) => item switch
    {
        ItemKind.Burst => Prize.Burst,
        ItemKind.Repel => Prize.Repel,
        ItemKind.Thor => Prize.Thor,
        ItemKind.Brick => Prize.Brick,
        ItemKind.Decoy => Prize.Decoy,
        ItemKind.Rocket => Prize.Rocket,
        ItemKind.Portal => Prize.Portal,
        _ => null,
    };

    /// <summary>
    /// Called by the registry on every position packet from a player participating in this match.
    /// Drives two things during pre-GO: (1) idle detection during Staging (used to fail the match
    /// if a player never moves), and (2) drift enforcement during Staging or Countdown -- if the
    /// player has wandered more than <see cref="QueueDefinition.MaxSpawnDriftTiles"/> tiles from
    /// their team's chosen spawn, they're warped back. Both are no-ops once the match goes Live.
    /// </summary>
    public void OnPositionPacket(PlayerKey key, sbyte rotation, short x, short y, WeaponCodes weapon)
    {
        if (Phase != MatchPhase.Staging && Phase != MatchPhase.Countdown) return;

        if (_drift.ShouldWarpBack(key, x, y, out var spawn) && _resolver.Resolve(key) is { } drifter)
        {
            _game.WarpTo(drifter, spawn.X, spawn.Y);
            if (_verbose.IsDebug)
            {
                // Position packet x/y are pixels; spawn is tiles (16 px each).
                int dxTiles = (x >> 4) - spawn.X, dyTiles = (y >> 4) - spawn.Y;
                _verbose.Debug(LogCategory,
                    $"Match {_matchId:N}: warped {key.Name} back to spawn ({spawn.X},{spawn.Y}) -- " +
                    $"drift {(int)Math.Sqrt((long)dxTiles * dxTiles + (long)dyTiles * dyTiles)}t.");
            }
        }

        if (Phase != MatchPhase.Staging) return;
        if (_idleTracker.RecordPosition(key, rotation, x, y, weapon)
            && _resolver.Resolve(key) is { } readyPlayer)
        {
            // First detected movement after placement -- confirm to the player so they know
            // the readiness check has registered and they don't need to keep wiggling.
            _chat.SendMessage(readyPlayer, "Got it -- you're ready. Standby for the countdown.");
        }
    }

    private bool OnStagingEnd()
    {
        // Top-level guard: a throw here would propagate into SS's mainloop-timer machinery and
        // potentially crash the server. Log and swallow instead -- the match will time out
        // naturally via the engine's join-timeout if we couldn't transition.
        try
        {
            var afk = _idleTracker.GetStillIdle();

            if (afk.Count > 0)
            {
                string afkNames = string.Join(", ", afk.Select(k => k.Name));
                // The AFK-specific line is a personal "you were flagged" notice, so we send it
                // directly to the affected participants only -- spectators don't need it.
                for (int i = 0; i < afk.Count; i++)
                    if (_resolver.Resolve(afk[i]) is { } p)
                        _chat.SendMessage(p, "You were flagged as AFK and the match was cancelled.");

                // Match-cancellation announcement for everyone else (participants and watching
                // spectators), excluding the AFK players who already got their own version.
                var notAfkParticipants = new List<Player>();
                for (int t = 0; t < _proposal.Teams.Count; t++)
                    for (int j = 0; j < _proposal.Teams[t].Count; j++)
                    {
                        var k = _proposal.Teams[t][j];
                        if (afk.Contains(k)) continue;
                        if (_resolver.Resolve(k) is { } p) notAfkParticipants.Add(p);
                    }
                string cancelMessage =
                    $"Match cancelled. {afkNames} did not ready. " +
                    "You've been moved to the front of the queue.";
                if (_audience is not null)
                    _audience.Broadcast(_matchId, _queue.MatchArenaName, notAfkParticipants, cancelMessage);
                else
                    foreach (var p in notAfkParticipants) _chat.SendMessage(p, cancelMessage);

                // Drive non-AFK participants to Active before cancelling. PlayerStateObserver only
                // fires OnPlayerJoinedArena on a spec->active ship change, so a participant who was
                // already in a non-spec ship at placement time stays Pending in the engine. Without
                // this, FinalizeCancellation's "Pending = no-show" sweep marks them as abandoners
                // alongside the genuine AFKs.
                for (int t = 0; t < _proposal.Teams.Count; t++)
                    for (int j = 0; j < _proposal.Teams[t].Count; j++)
                    {
                        var k = _proposal.Teams[t][j];
                        if (afk.Contains(k)) continue;
                        _engine.OnPlayerJoinedArena(k, _clock.UtcNow);
                    }
                _engine.CancelMatchAsAfk(_matchId, afk, _clock.UtcNow);
                // Cleanup is invoked by the registry's OnMatchEnded handler.
                return false;
            }

            // All players ready -- drive the engine into Live by reporting JoinedArena for each.
            for (int t = 0; t < _proposal.Teams.Count; t++)
                for (int j = 0; j < _proposal.Teams[t].Count; j++)
                    _engine.OnPlayerJoinedArena(_proposal.Teams[t][j], _clock.UtcNow);

            SetPhase(MatchPhase.Countdown);
            // CountdownDuration is validated at >= 5s in QueueDefinition, so the per-second
            // "-3-/-2-/-1-" tick window always has room to fire.
            _countdownSecondsRemaining = (int)_queue.CountdownDuration.TotalSeconds;
            _timer.SetTimer(OnCountdownTick, 1000, 1000, this);

            // For long countdowns (>10s), tell players how long they're waiting; for short ones,
            // the "-3-" tick is close enough that an explicit duration would just be noise.
            BroadcastToAll(_countdownSecondsRemaining > 10
                ? $"All set! Starting in {_countdownSecondsRemaining} seconds!"
                : "All set!");

            // If CountdownDuration is short enough that the advisor's ShipChangeAllowedUntil
            // already expired at staging-end, the lock is in effect right now. Fire the notice
            // immediately so players see the lock cue alongside "All set!". For longer countdowns
            // the matching tick in OnCountdownTick fires it.
            if (_queue.CountdownDuration <= MatchFreqAdvisor.ShipLockBeforeStart)
                SendPersonalToParticipants("Locked you to your current ship.");
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"Match {_matchId:N}: OnStagingEnd failed: {ex}");
        }
        return false;
    }

    /// <summary>Match has ended -- unlock and return players to spec.</summary>
    /// <param name="summary">Match-end announcement. Pass null when the caller has already
    /// broadcast a tailored line (e.g. AFK cancellation) so we don't double-message.</param>
    public void Cleanup(string? summary)
    {
        SetPhase(MatchPhase.Cleanup);
        _timer.ClearTimer(OnStagingEnd, this);
        _timer.ClearTimer(OnCountdownTick, this);
        // Cancel any in-flight knockout-spec deferrals so we don't race with the immediate
        // match-end spec below (TState-typed and untyped variants are tracked separately).
        _timer.ClearTimer<PlayerKey>(OnDeferredKnockoutSpec, this);
        _pendingKnockoutSpec.Clear();

        // Free the freq slot back to the rotating pool so a future match in the same arena can
        // pick it up after we've cycled past it. Symmetric with the BeginSetup allocate.
        _freqAllocator?.Release(_matchId);

        // Broadcast the summary to everyone (participants + focused spectators) before
        // returning the participants to spec so watchers learn the outcome too.
        if (!string.IsNullOrEmpty(summary))
            BroadcastToAll(summary);

        // Mirror CaptainsMatch.EndMatch: send participants still in the match arena to the
        // arena spec freq, not just ShipType.Spec. SetShip(Spec) alone leaves them stranded on
        // the (private) team freq, which the FreqManager then refuses to let them ship up off
        // of -- they'd have to ?go to escape. Includes the "already in spec but on team freq"
        // case (e.g. knocked-out players the orchestrator already specced) so they migrate too.
        // Participants who have wandered to a different arena are left alone -- whatever they're
        // doing there is no longer this match's business.
        var arena = string.IsNullOrEmpty(_queue.MatchArenaName)
            ? null
            : _arenaManager.FindArena(_queue.MatchArenaName);
        if (arena is null) return;

        short specFreq = arena.SpecFreq;
        for (int t = 0; t < _proposal.Teams.Count; t++)
        {
            for (int j = 0; j < _proposal.Teams[t].Count; j++)
            {
                if (_resolver.Resolve(_proposal.Teams[t][j]) is not { } p) continue;
                if (p.Arena != arena) continue;
                if (p.Ship != ShipType.Spec || p.Freq != specFreq)
                    _game.SetShipAndFreq(p, ShipType.Spec, specFreq);
            }
        }
    }

    /// <summary>
    /// Players that have been knocked out and are awaiting their <see cref="QueueDefinition.KnockoutSpecDelay"/>
    /// timer to spec them. Used to (a) suppress duplicate scheduling on extra kills against the
    /// same player and (b) match the deferred timer back to a player at fire time.
    /// </summary>
    private readonly HashSet<PlayerKey> _pendingKnockoutSpec = new();

    /// <summary>
    /// Called by <see cref="MatchOrchestratorRegistry"/> on every kill where the victim belongs
    /// to this match. If the kill exhausted the victim's last life, either spec immediately or
    /// schedule a deferred spec based on <see cref="QueueDefinition.KnockoutSpecDelay"/>.
    /// </summary>
    public void OnKill(PlayerKey victim)
    {
        if (Phase is MatchPhase.Cleanup) return;
        if (!_engine.ActiveMatches.TryGetValue(_matchId, out var match)) return;
        if (!match.LivesPerPlayer.HasValue) return;
        if (!match.ExitedAt.ContainsKey(victim)) return;

        // KillCallback fires once per kill, but defensively guard against repeats (e.g. the kill
        // packet replayed by some other path). Already-pending spec? leave the timer alone.
        if (_pendingKnockoutSpec.Contains(victim)) return;

        var delay = _queue.KnockoutSpecDelay;
        if (delay <= TimeSpan.Zero)
        {
            ForceSpec(victim);
            return;
        }

        _pendingKnockoutSpec.Add(victim);
        _timer.SetTimer(OnDeferredKnockoutSpec, (int)delay.TotalMilliseconds, Timeout.Infinite, victim, this);
    }

    /// <summary>Specs the victim if they're still resolvable and not already in spec.</summary>
    private void ForceSpec(PlayerKey victim)
    {
        if (_resolver.Resolve(victim) is { } p && p.Ship != ShipType.Spec)
        {
            _game.SetShip(p, ShipType.Spec);
        }
    }

    /// <summary>
    /// Deferred-spec timer body. One-shot (returns false). Cleanup() and the typed
    /// <c>ClearTimer&lt;PlayerKey&gt;</c> call cancel any in-flight instance to prevent racing
    /// with the immediate match-end spec.
    /// </summary>
    private bool OnDeferredKnockoutSpec(PlayerKey victim)
    {
        try
        {
            if (Phase is MatchPhase.Cleanup) return false;  // Cleanup already specced everyone.
            _pendingKnockoutSpec.Remove(victim);
            ForceSpec(victim);
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"Match {_matchId:N}: deferred knockout-spec for {victim.Name} failed: {ex}");
        }
        return false;
    }

    /// <summary>
    /// One tick per second of the pre-match countdown. Broadcasts the final-3s ticks, then
    /// "GO!" on the final tick and transitions the match to Live.
    /// </summary>
    private bool OnCountdownTick()
    {
        // Top-level guard: a throw here would propagate into SS's mainloop-timer machinery.
        try
        {
            _countdownSecondsRemaining--;
            if (_countdownSecondsRemaining > 0)
            {
                // Lock cue fires the instant the advisor's ShipChangeAllowedUntil expires --
                // ShipLockBeforeStart seconds before GO. Stays in sync with MatchFreqAdvisor's
                // OnMatchProposed lockOffset arithmetic.
                if (_countdownSecondsRemaining == (int)MatchFreqAdvisor.ShipLockBeforeStart.TotalSeconds)
                    SendPersonalToParticipants("Locked you to your current ship.");
                // Only the last 3 ticks are announced -- earlier ticks would clutter chat for
                // longer countdowns where the up-front "Starting in N seconds!" already covered it.
                if (_countdownSecondsRemaining <= 3)
                    BroadcastToAll($"-{_countdownSecondsRemaining}-");
                return true;
            }

            SetPhase(MatchPhase.Live);
            // Engine-side Forming -> Live happens here, not during placement: the engine treats
            // "Live" as gameplay-live, so no Live-only state (kill processing, team-collapse, etc.)
            // can fire pre-GO even if a ship-lock expires early.
            _engine.MarkMatchLive(_matchId, _clock.UtcNow);
            // The "GO!" announcement reaches participants and focused spectators alike.
            // Mirror upstream TeamVersusMatch's start cue: the message carries a Ding so players
            // get an audible "match has started" beat in addition to the chat line.
            BroadcastToAll("GO!", ChatSound.Ding);
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"Match {_matchId:N}: OnCountdownTick failed: {ex}");
        }
        return false;
    }

    /// <summary>Sends <paramref name="message"/> to every resolvable participant and to any
    /// spectator currently focused on this match (per <c>IMatchFocus</c>). Pass <paramref name="sound"/>
    /// to attach a chat sound (e.g. <see cref="ChatSound.Ding"/> for the GO! announcement).</summary>
    private void BroadcastToAll(string message, ChatSound sound = ChatSound.None)
    {
        var participants = ResolveParticipants();
        if (_audience is null)
        {
            foreach (var p in participants)
            {
                if (sound == ChatSound.None) _chat.SendMessage(p, message);
                else _chat.SendMessage(p, sound, message);
            }
            return;
        }
        _audience.Broadcast(_matchId, _queue.MatchArenaName, participants, message, sound);
    }

    /// <summary>
    /// Deliver <paramref name="message"/> to each resolvable participant as a true RemotePrivate
    /// chat line, mirroring SS.Matchmaking.TeamVersusMatch's ready-up notice
    /// (<c>TeamVersusMatch.cs:5723</c>): biller-routed, sender = the player's own name. Continuum
    /// renders this as an inbound ":theirname:message" line at the top of chat -- visually
    /// distinct from arena chatter and from the prior "(theirname)>message" self-echo, which
    /// players were missing.
    /// </summary>
    private void SendDmToParticipants(string message)
    {
        var set = new HashSet<Player>();
        foreach (var p in ResolveParticipants())
        {
            set.Clear();
            set.Add(p);
            _chat.SendRemotePrivMessage(set, ChatSound.None, [], p.Name, message);
        }
    }

    /// <summary>
    /// Send <paramref name="message"/> to each resolvable participant as a yellow personal
    /// arena message -- visible only to that player, but without DM framing. Used for in-match
    /// status notices that don't need to grab attention the way the move prompt does.
    /// </summary>
    private void SendPersonalToParticipants(string message)
    {
        foreach (var p in ResolveParticipants())
            _chat.SendMessage(p, message);
    }

    /// <summary>
    /// Send <paramref name="message"/> to focused spectators only, suppressing delivery to
    /// participants. Used when participants get a different (DM-style) version of the same
    /// information so they don't see it twice. No-op if the audience helper isn't wired.
    /// </summary>
    private void BroadcastToSpectators(string message)
    {
        if (_audience is null) return;
        _audience.Broadcast(_matchId, _queue.MatchArenaName, Array.Empty<Player>(), message);
    }

    private List<Player> ResolveParticipants()
    {
        var list = new List<Player>(_proposal.Teams.Count * 2);
        for (int t = 0; t < _proposal.Teams.Count; t++)
            for (int j = 0; j < _proposal.Teams[t].Count; j++)
                if (_resolver.Resolve(_proposal.Teams[t][j]) is { } p)
                    list.Add(p);
        return list;
    }

    /// <summary>
    /// Called when one of this match's teams has lost all live members and the team-collapse
    /// grace timer just started. Broadcasts a warning to everyone so the surviving teams know
    /// what's happening and a returning teammate knows they have a window to recover.
    /// </summary>
    public void OnTeamCollapsing(int teamIdx, TimeSpan forfeitIn)
    {
        if (teamIdx < 0 || teamIdx >= _proposal.Teams.Count) return;
        if (Phase != MatchPhase.Live) return;
        var teamLabel = TeamLabel(teamIdx);
        BroadcastToAll(
            $"Team {teamLabel} has no players in the arena -- forfeiting in {(int)forfeitIn.TotalSeconds}s if no one returns.");
    }

    /// <summary>Called when a collapsing team got at least one player back before the grace expired.</summary>
    public void OnTeamRecovered(int teamIdx)
    {
        if (teamIdx < 0 || teamIdx >= _proposal.Teams.Count) return;
        if (Phase != MatchPhase.Live) return;
        BroadcastToAll($"Team {TeamLabel(teamIdx)} is back. Match continues.");
    }

    private string TeamLabel(int teamIdx)
    {
        var team = _proposal.Teams[teamIdx];
        var names = new string[team.Count];
        for (int i = 0; i < team.Count; i++) names[i] = team[i].Name;
        return string.Join("/", names);
    }

    private ShipType ShipFor(int teamIdx, int slotIdx)
    {
        if (_queue.ShipBySlot is null) return ShipType.Warbird;
        var raw = _queue.ShipBySlot[teamIdx][slotIdx];
        if (raw < 0 || raw > 7) return ShipType.Warbird;
        return (ShipType)raw;
    }

}
