using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
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
/// and freq, warps to the per-team start location, overrides each client's in-match respawn box,
/// runs an idle-detection staging phase, then a countdown, and returns players to spec on
/// completion. One instance per active match.
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

    /// <summary>Applies/clears the per-player client-settings <c>[Spawn]</c> override that controls
    /// where the client respawns participants during the match. <see langword="null"/> in test
    /// paths that construct the orchestrator without the SS client-settings edge.</summary>
    private readonly SpawnSettingsApplier? _spawnApplier;

    /// <summary>Applies/clears the per-player client-settings item-max override that implements the
    /// game type's <c>DisallowItems</c> no-items mode. <see langword="null"/> in test paths that
    /// construct the orchestrator without the SS client-settings edge.</summary>
    private readonly NoItemsSettingsApplier? _noItemsApplier;

    /// <summary>Participants who currently have a respawn override applied (tracked so we only send
    /// the large client-settings packet to clear it for players we actually overrode).</summary>
    private readonly HashSet<PlayerKey> _respawnOverridden = new();

    /// <summary>Participants who currently have the no-items item-max override applied (same
    /// only-clear-what-we-overrode rationale as <see cref="_respawnOverridden"/>).</summary>
    private readonly HashSet<PlayerKey> _noItemsOverridden = new();

    /// <summary>Per-player ship the participant was on at the moment they last specced themselves
    /// out. Populated by <see cref="OnPlayerSpecced"/>; consumed by <see cref="TryReturn"/> so the
    /// returner re-enters in the exact ship they left, preserving any post-death ship-change they
    /// made within the grace window. Cleared on successful return.</summary>
    private readonly Dictionary<PlayerKey, ShipType> _shipAtLeave = new();

    /// <summary>Zone-wide per-player "last ship" memory (fed by
    /// <see cref="ClashEngine.Adapter.LastShipTracker"/>, persisted via IPersist). Consulted at
    /// placement so each player starts in the ship they last flew. <see langword="null"/> in test
    /// paths that construct the orchestrator without it; placement then defaults to Warbird.</summary>
    private readonly Persistence.PersistLastShipStore? _lastShips;

    /// <summary>Team-0 freq for this match; team-t uses <c>_freqBase + t * 100</c>. Defaults to
    /// the legacy 100/200/... convention until <see cref="BeginSetup"/> reserves a rotating base
    /// from <see cref="MatchFreqAllocator"/>. Read by <see cref="MatchFreqAdvisor"/> and the LVZ
    /// team adapter via the allocator so they all see the same numbers.</summary>
    private short _freqBase = MatchFreqAllocator.BaseFreq;

    /// <summary>Staging-phase idle detection (per-player).</summary>
    private readonly IdleStateTracker _idleTracker = new();

    /// <summary>Match-start pick + pre-GO drift-back enforcement.</summary>
    private readonly StartDriftEnforcer _drift;

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

    private readonly record struct PlacementInfo(ShipType Ship, short Freq, int TeamIdx, short StartX, short StartY);

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
        IComponentBroker? broker = null,
        SpawnSettingsApplier? spawnApplier = null,
        NoItemsSettingsApplier? noItemsApplier = null,
        Persistence.PersistLastShipStore? lastShips = null)
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
        _spawnApplier = spawnApplier;
        _noItemsApplier = noItemsApplier;
        _lastShips = lastShips;
        _drift = new StartDriftEnforcer(_queue, _proposal);

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

    /// <summary>
    /// True once one of this orchestrator's own cancellation paths (<see cref="OnStagingEnd"/>'s
    /// no-show branch or <see cref="AbandonForAbsenteesAtGo"/>) has broadcast a tailored
    /// "Match cancelled ..." line naming the responsible player(s). The registry consults this in
    /// <c>OnMatchEnded</c>: a Cancelled match the orchestrator did NOT announce -- i.e. the engine's
    /// join-timeout backstop fired without the orchestrator reaching a cancellation itself -- still
    /// gets a generic notice instead of dying silently.
    /// </summary>
    public bool HasAnnouncedCancellation { get; private set; }

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

        _drift.ChooseStartForEachTeam(_proposal, _rng);

        // Reserve a rotating freq base for this match so concurrent matches in the same arena
        // don't all stack their teams on freqs 100/200. Falls back to the static convention when
        // no allocator was injected (e.g. test paths constructing the orchestrator directly).
        _freqBase = _freqAllocator?.Allocate(_matchId, arenaName, _proposal.Teams.Count)
            ?? MatchFreqAllocator.BaseFreq;

        for (int t = 0; t < _proposal.Teams.Count; t++)
        {
            short freq = (short)(_freqBase + t * MatchFreqAllocator.FreqStep);
            var start = _drift.ChosenStart(t);
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

                var ship = PreferredShip(key);
                _pendingPlacement[key] = new PlacementInfo(ship, freq, t, start.X, start.Y);

                if (arenaName is not null && !IsInArena(player, arenaName))
                {
                    // Different arena (or no arena yet): transfer asynchronously. The placement
                    // (ship + freq + warp + lock) finishes when EnterArena fires for them, via
                    // the registry's PlayerActionCallback dispatcher -> OnPlayerEnteredArena.
                    // SendToArena's start args are tile coords (and it silently ignores any >=
                    // 1024); StartPoint is tiles, so TileX/TileY hand it the value directly.
                    _arenaManager.SendToArena(player, arenaName, start.TileX, start.TileY);
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

        // Mirror SS.Matchmaking.TeamVersusMatch:4842: send the match-found notice to each
        // participant as a senderless RemotePrivate so Continuum plays its standard incoming-
        // private "beep" (same sound players hear when TeamVersus drops them into a match).
        // Spectators get a plain arena broadcast since they don't need the call-to-action.
        var notice =
            $"You have {(int)_queue.StagingDuration.TotalSeconds} seconds to move or fire to confirm you're here. " +
            "You may change ships freely; ships are locked 5s before GO.";
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

    /// <summary>The ship to place <paramref name="key"/> on when entering a match: the last ship
    /// they were observed flying before going to spec (zone-wide, persisted), falling back to
    /// Warbird when no memory exists (first-time players, or no store wired).</summary>
    private ShipType PreferredShip(PlayerKey key) =>
        _lastShips?.Get(key) is { } remembered && remembered != ShipType.Spec
            ? remembered
            : ShipType.Warbird;

    private void PlacePlayerOnShip(PlayerKey key, Player player)
    {
        if (!_pendingPlacement.Remove(key, out var info)) return;

        // Push the client-settings overrides BEFORE the ship-up so the client already has the
        // match's [Spawn] box (and zeroed item maxes in no-items mode) when it spawns the ship.
        // For the initial placement the WarpTo below moves them onto the exact start location
        // regardless; the respawn override governs every subsequent respawn after a death.
        ApplyClientSettingOverrides(key, info.TeamIdx, player);

        _game.SetShipAndFreq(player, info.Ship, info.Freq);
        if (info.StartX != 0 || info.StartY != 0)
        {
            // StartX/Y are tile coords (the documented Team<t>Starts contract); IGame.WarpTo takes
            // tiles, so hand them straight through.
            _game.WarpTo(player, info.StartX, info.StartY);
            // Anchor the idle tracker at the warp destination so stale pre-warp position
            // packets (in-flight when WarpTo went out) don't seed the tracker at the old
            // position and trigger a false-positive "moved" detection on the first post-warp
            // packet. The anchor lives in position-packet pixel space, so scale the tile start
            // up by 16.
            _idleTracker.AnchorAt(key, (short)(info.StartX << 4), (short)(info.StartY << 4));
        }
        // No SS-Core IGame.Lock during setup -- the MatchFreqAdvisor enforces freq lock from
        // proposal time and opens a ship-change window for the staging duration so participants
        // can pick their loadout. Lock is re-enforced by the advisor for Countdown / Live.

        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"Match {_matchId:N}: placed {key.Name} on {info.Ship} freq {info.Freq} at ({info.StartX},{info.StartY}).");
    }

    /// <summary>
    /// Apply every client-settings override this match calls for to <paramref name="player"/> --
    /// the team's respawn box (when one is configured) and the no-items item-max zeroing (when the
    /// game type sets <c>DisallowItems</c>) -- then push at most one client-settings packet
    /// covering both. A player who needs no override never gets a packet.
    /// </summary>
    private void ApplyClientSettingOverrides(PlayerKey key, int teamIdx, Player player)
    {
        bool spawn = ApplyRespawnOverride(key, teamIdx, player);
        bool items = ApplyNoItemsOverride(key, player);
        if (spawn) _spawnApplier!.Send(player);
        else if (items) _noItemsApplier!.Send(player);
    }

    /// <summary>
    /// Stage (without sending) the queue's per-team respawn box for <paramref name="teamIdx"/> on
    /// <paramref name="player"/> via the client-settings <c>[Spawn]</c> override, recording that we
    /// did so. Returns whether an override was staged; no-op (false) when no box is configured for
    /// that team or the applier is absent / not ready.
    /// </summary>
    private bool ApplyRespawnOverride(PlayerKey key, int teamIdx, Player player)
    {
        if (_spawnApplier is not { Ready: true }) return false;
        if (_queue.SpawnByTeam is not { } byTeam) return false;
        if (teamIdx < 0 || teamIdx >= byTeam.Count) return false;
        if (byTeam[teamIdx] is not { } area) return false;

        _spawnApplier.Apply(player, area, send: false);
        _respawnOverridden.Add(key);
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"Match {_matchId:N}: applied respawn override for {key.Name} -> " +
                $"({area.Center.X},{area.Center.Y}) r{area.RadiusTiles}t.");
        return true;
    }

    /// <summary>
    /// Stage (without sending) the no-items override -- every ship's item-max client settings
    /// zeroed -- on <paramref name="player"/>, recording that we did so. Returns whether the
    /// override was staged; no-op (false) when the game type allows items or the applier is
    /// absent / not ready.
    /// </summary>
    private bool ApplyNoItemsOverride(PlayerKey key, Player player)
    {
        if (_noItemsApplier is not { Ready: true }) return false;
        if (!_queue.DisallowItems) return false;

        _noItemsApplier.Apply(player, send: false);
        _noItemsOverridden.Add(key);
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory, $"Match {_matchId:N}: applied no-items override for {key.Name}.");
        return true;
    }

    /// <summary>
    /// Clear any client-settings overrides (respawn box and/or no-items) previously applied to
    /// <paramref name="key"/>, pushing at most one client-settings packet. No-op if we never
    /// applied any (so no redundant packet). Resolves the player fresh so it works from the
    /// spec / cleanup paths.
    /// </summary>
    private void ClearClientSettingOverrides(PlayerKey key)
    {
        bool hadSpawn = _respawnOverridden.Remove(key);
        bool hadItems = _noItemsOverridden.Remove(key);
        if (!hadSpawn && !hadItems) return;
        if (_resolver.Resolve(key) is not { } player) return;

        // Keys only enter the tracking sets when the corresponding applier was present and ready,
        // so the null-forgiving derefs below are safe.
        if (hadSpawn) _spawnApplier!.Clear(player, send: false);
        if (hadItems) _noItemsApplier!.Clear(player, send: false);
        if (hadSpawn) _spawnApplier!.Send(player);
        else _noItemsApplier!.Send(player);

        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"Match {_matchId:N}: cleared client-setting overrides for {key.Name} " +
                $"(respawn={hadSpawn}, noItems={hadItems}).");
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

        int teamIdx = -1;
        for (int t = 0; t < _proposal.Teams.Count && teamIdx < 0; t++)
        {
            for (int j = 0; j < _proposal.Teams[t].Count; j++)
            {
                if (_proposal.Teams[t][j] == key) { teamIdx = t; break; }
            }
        }
        if (teamIdx < 0) return ReturnResult.MatchEnded;

        short freq = (short)(_freqBase + teamIdx * MatchFreqAllocator.FreqStep);
        // Prefer the ship the player was actually on at the moment they specced -- preserves any
        // post-death ship-change made within the grace window. Falls back to the player's
        // zone-wide last-ship memory (then Warbird) if the snapshot was lost.
        var ship = _shipAtLeave.TryGetValue(key, out var savedShip) && savedShip != ShipType.Spec
            ? savedShip
            : PreferredShip(key);
        // Re-apply the client-settings overrides BEFORE the ship-up: a returning player is not
        // warped to the start location, so the client must already hold the match's [Spawn] box
        // when it spawns the ship for them to come back inside the match arena (and the zeroed
        // item maxes in no-items mode, so the fresh ship spawns item-free).
        ApplyClientSettingOverrides(key, teamIdx, player);
        _game.SetShipAndFreq(player, ship, freq);

        // Apply the game type's items policy to the freshly-respawned loadout.
        ApplyReturnItemsAction(player, key);

        // Snapshot consumed; clear so the next leave-cycle isn't tainted by a stale value.
        _shipAtLeave.Remove(key);

        // Drive SS.Matchmaking.MatchFocus through a PlayingInMatch null cycle so MatchLvz
        // re-sends the statbox LVZ. MatchFocus.SetPlaying early-returns when the player is
        // already PlayingInMatch (which it is -- SS doesn't clear it on the active->spec
        // transition), so MatchFocusChangedCallback never fires and MatchLvz's
        // SetAndSendMatchLvz no-ops on newState==oldState. See RefreshMatchFocus.
        RefreshMatchFocus(key, player);

        // Mirror SS.Matchmaking.TeamVersusMatch:2762: announce the return to the match audience
        // (participants + focused spectators) with the items-action that was just applied and the
        // returner's remaining lives, so opponents see e.g.
        //   "Player returned to the match. [Items Restored] [Lives: 2]"
        var returnNotice = $"{key.Name} returned to the match.";
        var itemsDesc = GetItemsActionDescription(_queue.ReturnItemsAction);
        if (!string.IsNullOrEmpty(itemsDesc))
            returnNotice += $" [{itemsDesc}]";
        if (match.LivesPerPlayer.HasValue && match.LivesRemaining.TryGetValue(key, out var lives))
            returnNotice += $" [Lives: {lives}]";
        BroadcastToAll(returnNotice);

        // If this player specced before the countdown's GO! tick, the engine deferred its
        // Forming->Live flip (MarkMatchLive at GO! armed a pending go-live). We deliberately do NOT
        // retry MarkMatchLive here: SetShipAndFreq above drives engine.OnPlayerReturned via an
        // asynchronous ship-change callback that fires on a later mainloop iteration, so from the
        // engine's view the player is still in spec at this point and a retry would no-op. The
        // engine completes the deferred go-live off that async return instead.
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"Match {_matchId:N}: returned {key.Name} to {ship} freq {freq} " +
                $"(items={_queue.ReturnItemsAction}).");
        return ReturnResult.Placed;
    }

    /// <summary>
    /// Human-facing label for an <see cref="ItemsAction"/>, mirroring
    /// SS.Matchmaking.TeamVersusMatch's <c>GetItemsActionDescription</c> verbatim so the
    /// "?return" broadcast in the two modules reads identically. Used inside the
    /// <c>[...]</c> bracket of the return notice.
    /// </summary>
    private static string GetItemsActionDescription(ItemsAction action) => action switch
    {
        ItemsAction.Full => "Full Ship",
        ItemsAction.Burn => "Items Burned",
        ItemsAction.Restore => "Items Restored",
        _ => string.Empty,
    };

    /// <summary>
    /// Force-refresh the returning participant's MatchLvz state by walking the broker's
    /// <see cref="IMatchFocusAdvisor"/> set, then firing <see cref="MatchRemovePlayingCallback"/>
    /// followed by <see cref="MatchAddPlayingCallback"/> for that match. The forced Remove->Add
    /// cycle is load-bearing: SS.Matchmaking.MatchFocus.SetPlaying early-returns when
    /// <c>PlayingInMatch</c> already equals the target (it does -- MatchFocus doesn't clear
    /// PlayingInMatch on active->spec), and MatchLvz only redraws via
    /// <c>MatchFocusChangedCallback</c>, which only fires on a real state transition. Routing
    /// through null in between guarantees the change-callback fires twice and MatchLvz's
    /// <c>SetAndSendMatchLvz</c> sees a different (newState, oldState) pair on the second call,
    /// breaking past its own no-op guard. No-op if the broker wasn't injected (test paths) or no
    /// advisor returned a match for this player.
    /// </summary>
    private void RefreshMatchFocus(PlayerKey key, Player player)
    {
        if (_broker is null) return;
        var advisors = _broker.GetAdvisors<IMatchFocusAdvisor>();
        foreach (var advisor in advisors)
        {
            var match = advisor.GetMatch(player);
            if (match is null) continue;
            MatchRemovePlayingCallback.Fire(_broker, match, key.Name, player);
            MatchAddPlayingCallback.Fire(_broker, match, key.Name, player);
            if (_verbose.IsDebug)
                _verbose.Debug(LogCategory,
                    $"Match {_matchId:N}: refreshed match focus for {key.Name} (remove+add).");
            return;
        }
        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"Match {_matchId:N}: no IMatchFocusAdvisor returned a match for {key.Name}; statbox refresh skipped.");
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
    /// in-match participant, and on LeaveArena for a participant still in a ship (an arena exit
    /// fires no ship->spec change of its own). No-op for players we no longer own (e.g. already
    /// eliminated) and for players already in spec (preserving the snapshot taken when they
    /// specced, for the spec-then-leave-arena sequence).
    /// </summary>
    public void OnPlayerSpecced(PlayerKey key, ShipType prevShip)
    {
        if (!OwnsPlayer(key)) return;
        if (prevShip == ShipType.Spec) return;
        _shipAtLeave[key] = prevShip;

        // A ship->spec transition (self-spec or a knockout ForceSpec, both of which funnel through
        // here) takes the player out of the match's flow, so drop their client-setting overrides
        // ([Spawn] box and no-items item maxes). A normal mid-match death does NOT fire this
        // (Continuum respawns the ship without a spec transition), so the overrides correctly
        // persist across respawns. ?return re-applies them.
        ClearClientSettingOverrides(key);
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
    /// The per-item delta math (union of initial-loadout and saved-leave keys, and the
    /// Restore-without-snapshot degrade-to-Full fallback) lives in
    /// <see cref="ReturnItemsPlan.ComputeAdjustments"/> so it's unit-testable without SS types;
    /// this method just maps each computed delta onto a single positive- or negative-prize call.
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

        foreach (var (item, delta) in ReturnItemsPlan.ComputeAdjustments(_queue.ReturnItemsAction, initial, saved))
        {
            var prize = PrizeForItem(item);
            if (prize is null) continue;
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
    /// player has wandered more than <see cref="QueueDefinition.MaxStartDriftTiles"/> tiles from
    /// their team's chosen start, they're warped back. Both are no-ops once the match goes Live;
    /// during Live, packets feed the engine's zone-presence tracking instead (when the game type
    /// has a presence zone).
    /// </summary>
    public void OnPositionPacket(PlayerKey key, sbyte rotation, short x, short y, WeaponCodes weapon)
    {
        if (Phase == MatchPhase.Live)
        {
            // Zone-presence feed: packets are pixels, the engine's zone box is tiles (16 px/tile).
            // Only forwarded when this match's game type actually has a zone, so games without
            // one pay nothing per packet.
            if (_queue.PresenceZone is not null)
                _engine.OnPlayerPosition(key, x >> 4, y >> 4, _clock.UtcNow);
            return;
        }

        if (Phase != MatchPhase.Staging && Phase != MatchPhase.Countdown) return;

        if (_drift.ShouldWarpBack(key, x, y, out var start) && _resolver.Resolve(key) is { } drifter)
        {
            // start is tiles; WarpTo takes tiles.
            _game.WarpTo(drifter, start.TileX, start.TileY);
            if (_verbose.IsDebug)
            {
                // Position packet x/y are pixels; compare against the start's pixel form (16 px/tile).
                int dxPixels = x - start.PixelX, dyPixels = y - start.PixelY;
                int driftTiles = (int)(Math.Sqrt((long)dxPixels * dxPixels + (long)dyPixels * dyPixels) / 16);
                _verbose.Debug(LogCategory,
                    $"Match {_matchId:N}: warped {key.Name} back to start ({start.X},{start.Y}) -- " +
                    $"drift {driftTiles}t.");
            }
        }

        if (Phase != MatchPhase.Staging) return;
        if (_idleTracker.RecordPosition(key, rotation, x, y, weapon)
            && _resolver.Resolve(key) is { } readyPlayer)
        {
            // First detected movement after placement -- confirm to the player so they know
            // the readiness check has registered and they don't need to keep wiggling.
            _chat.SendMessage(readyPlayer, "Got it -- you're ready. Standby for the countdown.");

            // Staging is up-to StagingDuration: as soon as every participant has flipped
            // non-idle, short-circuit to the countdown instead of burning the rest of the
            // window. The timer-fired path remains the fallback for the AFK case;
            // OnStagingEnd is safe to call from this synchronous path because it just runs
            // its existing all-ready branch (afk.Count == 0). Clear the timer first so a
            // late-firing slot can't double-enter.
            if (Phase == MatchPhase.Staging && _idleTracker.GetStillIdle().Count == 0)
            {
                _timer.ClearTimer(OnStagingEnd, this);
                OnStagingEnd();
            }
        }
    }

    private bool OnStagingEnd()
    {
        // Top-level guard: a throw here would propagate into SS's mainloop-timer machinery and
        // potentially crash the server. Log and swallow instead -- the match will time out
        // naturally via the engine's join-timeout if we couldn't transition.
        try
        {
            var notReady = _idleTracker.GetStillIdle();

            if (notReady.Count > 0)
            {
                // Split the no-shows by *why* they failed staging. A participant sitting in SPEC
                // (dropped to, or never left, spectator) made a deliberate departure -- categorically
                // different from an AFK no-show who is in their ship but never moved. The idle tracker
                // can't tell them apart (a spec'd ship reports no movement either way), so we
                // partition on the player's current ship and give each group its own reason. A
                // resolve failure (disconnected) counts as departed -- they're gone, not idle.
                var departed = new List<PlayerKey>();
                var afk = new List<PlayerKey>();
                for (int i = 0; i < notReady.Count; i++)
                {
                    var k = notReady[i];
                    bool inShip = _resolver.Resolve(k) is { } pp && pp.Ship != ShipType.Spec;
                    (inShip ? afk : departed).Add(k);
                }

                // Personal notice to each cancellation-causer, tailored to why -- sent directly to
                // the affected participants only (spectators don't need it).
                for (int i = 0; i < departed.Count; i++)
                    if (_resolver.Resolve(departed[i]) is { } p)
                        _chat.SendMessage(p, "You left during staging, so the match was cancelled.");
                for (int i = 0; i < afk.Count; i++)
                    if (_resolver.Resolve(afk[i]) is { } p)
                        _chat.SendMessage(p, "You were flagged as AFK and the match was cancelled.");

                // Match-cancellation announcement for everyone else (participants and watching
                // spectators), excluding the no-show players who already got their own version. The
                // reason names each group: "<X> left" for deliberate specs, "<Y> did not ready" for
                // AFKs, joined when both occurred.
                var stillHere = new List<Player>();
                for (int t = 0; t < _proposal.Teams.Count; t++)
                    for (int j = 0; j < _proposal.Teams[t].Count; j++)
                    {
                        var k = _proposal.Teams[t][j];
                        if (notReady.Contains(k)) continue;
                        if (_resolver.Resolve(k) is { } p) stillHere.Add(p);
                    }
                var reasons = new List<string>(2);
                if (departed.Count > 0)
                    reasons.Add($"{string.Join(", ", departed.Select(k => k.Name))} left");
                if (afk.Count > 0)
                    reasons.Add($"{string.Join(", ", afk.Select(k => k.Name))} did not ready");
                string cancelMessage =
                    $"Match cancelled. {string.Join("; ", reasons)}. " +
                    "You've been moved to the front of the queue.";
                if (_audience is not null)
                    _audience.Broadcast(_matchId, _queue.MatchArenaName, stillHere, cancelMessage);
                else
                    foreach (var p in stillHere) _chat.SendMessage(p, cancelMessage);
                HasAnnouncedCancellation = true;

                // Drive still-here participants to Active before cancelling. PlayerStateObserver only
                // fires OnPlayerJoinedArena on a spec->active ship change, so a participant who was
                // already in a non-spec ship at placement time stays Pending in the engine. Without
                // this, FinalizeCancellation's "Pending = no-show" sweep marks them as abandoners
                // alongside the genuine no-shows.
                for (int t = 0; t < _proposal.Teams.Count; t++)
                    for (int j = 0; j < _proposal.Teams[t].Count; j++)
                    {
                        var k = _proposal.Teams[t][j];
                        if (notReady.Contains(k)) continue;
                        _engine.OnPlayerJoinedArena(k, _clock.UtcNow);
                    }
                _engine.CancelMatchAsAfk(_matchId, notReady, _clock.UtcNow);
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

            // For countdowns with a pre-lock ship-pick window (CountdownDuration >
            // ShipLockBeforeStart), tell players how many seconds they have to finalize their
            // ship. For shorter countdowns the lock is immediate and the "-3-" tick is close
            // enough that an explicit duration would be noise.
            int pickSeconds = _countdownSecondsRemaining - (int)MatchFreqAdvisor.ShipLockBeforeStart.TotalSeconds;
            BroadcastToAll(pickSeconds > 0
                ? $"All set! Pick your final ship -- {pickSeconds}s until lock, then GO."
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

        // Revert any still-active client-setting overrides so departing participants don't keep
        // the match's [Spawn] box or zeroed item maxes if they ship up elsewhere later. Snapshot
        // the sets since ClearClientSettingOverrides mutates them. (Most participants were already
        // cleared when they were specced; this catches the rest -- e.g. winners still in their
        // ships at match end.)
        if (_respawnOverridden.Count > 0 || _noItemsOverridden.Count > 0)
        {
            var overridden = new HashSet<PlayerKey>(_respawnOverridden);
            overridden.UnionWith(_noItemsOverridden);
            foreach (var k in overridden)
                ClearClientSettingOverrides(k);
        }

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

            // Engine-side Forming -> Live happens here, not during placement: the engine treats
            // "Live" as gameplay-live, so no Live-only state (kill processing, team-collapse, etc.)
            // can fire pre-GO even if a ship-lock expires early. MarkMatchLive returns false when a
            // rostered player isn't Active -- i.e. someone specced during the countdown and didn't
            // ?return in time. We don't start a match a player short: abandon it cleanly instead.
            if (_engine.MarkMatchLive(_matchId, _clock.UtcNow))
            {
                SetPhase(MatchPhase.Live);
                // The "GO!" announcement reaches participants and focused spectators alike.
                // Mirror upstream TeamVersusMatch's start cue: the message carries a Ding so players
                // get an audible "match has started" beat in addition to the chat line.
                BroadcastToAll("GO!", ChatSound.Ding);
            }
            else
            {
                AbandonForAbsenteesAtGo();
            }
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"Match {_matchId:N}: OnCountdownTick failed: {ex}");
        }
        return false;
    }

    /// <summary>
    /// Called at the countdown's GO! tick when <see cref="MatchmakingEngine.MarkMatchLive"/> reports
    /// the roster isn't all-Active -- a rostered player specced during the countdown and is still in
    /// spec at the start. Rather than begin the match a player short (or strand the engine in Forming
    /// until its join-timeout cancels it), abandon it now via
    /// <see cref="MatchmakingEngine.CancelForming"/>: the cancellation runs exactly as the
    /// join-timeout's would (abandonment assessed by the candidate rule, so a lone leaver who
    /// stranded no viable teammate stays penalty-free), just immediately. We name the absentee(s) in
    /// the cancel notice to everyone present so no one is left wondering why they were specced.
    /// </summary>
    private void AbandonForAbsenteesAtGo()
    {
        if (!_engine.ActiveMatches.TryGetValue(_matchId, out var match)) return;

        // Absentees are the players the engine doesn't see as Active (specced during the countdown);
        // everyone else is present and on their ship.
        var absent = new List<PlayerKey>();
        var present = new List<Player>();
        for (int t = 0; t < _proposal.Teams.Count; t++)
            for (int j = 0; j < _proposal.Teams[t].Count; j++)
            {
                var k = _proposal.Teams[t][j];
                if (match.GetStatus(k) == PlayerStatus.Active)
                {
                    if (_resolver.Resolve(k) is { } p) present.Add(p);
                }
                else
                {
                    absent.Add(k);
                }
            }

        // Defensive: MarkMatchLive only fails when someone isn't Active, so absent is non-empty in
        // practice. If physical state somehow disagrees, don't cancel for no one -- let the engine's
        // join-timeout be the backstop.
        if (absent.Count == 0) return;

        var names = string.Join(", ", absent.Select(k => k.Name));
        var cancelMessage =
            $"Match cancelled -- {names} {(absent.Count == 1 ? "wasn't" : "weren't")} on a ship at the start. " +
            "Use ?play to queue again.";
        if (_audience is not null)
            _audience.Broadcast(_matchId, _queue.MatchArenaName, present, cancelMessage);
        else
            foreach (var p in present) _chat.SendMessage(p, cancelMessage);
        HasAnnouncedCancellation = true;

        _engine.CancelForming(_matchId, _clock.UtcNow);
        // Cleanup is invoked by the registry's OnMatchEnded handler.
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
    /// Deliver <paramref name="message"/> to each resolvable participant as a senderless
    /// RemotePrivate chat line, mirroring SS.Matchmaking.TeamVersusMatch's match-found notice
    /// (<c>TeamVersusMatch.cs:4842</c>): <see cref="IChat.SendAnyMessage"/> with
    /// <see cref="ChatMessageType.RemotePrivate"/>, no <c>from</c>, and
    /// <see cref="ChatSound.None"/>. Continuum plays its standard incoming-private sound (the
    /// "beep") on receipt; with no sender it lands as a bare line rather than the prior
    /// "(theirname)>message" self-echo, which clients muted as an outbound echo.
    /// </summary>
    private void SendDmToParticipants(string message)
    {
        var set = new HashSet<Player>();
        foreach (var p in ResolveParticipants())
        {
            set.Clear();
            set.Add(p);
            _chat.SendAnyMessage(set, ChatMessageType.RemotePrivate, ChatSound.None, null, message);
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

        // Same line for every participant -- record it once as a single arena line in the
        // replay, mirroring how Broadcast-delivered lines are captured.
        _audience?.RecordArenaLine(_matchId, message);
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

    /// <summary>
    /// Called when a team has nobody inside the game type's presence zone and the forfeit clock
    /// is running. Mirrors <see cref="OnTeamCollapsing"/>: warn everyone so the absent team knows
    /// to get back and the opponents know a forfeit may be coming.
    /// </summary>
    public void OnZoneVacated(int teamIdx, TimeSpan forfeitIn)
    {
        if (teamIdx < 0 || teamIdx >= _proposal.Teams.Count) return;
        if (Phase != MatchPhase.Live) return;
        BroadcastToAll(
            $"Team {TeamLabel(teamIdx)} has left the zone -- back in {(int)forfeitIn.TotalSeconds}s or they forfeit!");
    }

    /// <summary>Called when a zone-vacant team got a player back inside the zone in time.</summary>
    public void OnZoneReclaimed(int teamIdx)
    {
        if (teamIdx < 0 || teamIdx >= _proposal.Teams.Count) return;
        if (Phase != MatchPhase.Live) return;
        BroadcastToAll($"Team {TeamLabel(teamIdx)} is back in the zone.");
    }

    private string TeamLabel(int teamIdx)
    {
        var team = _proposal.Teams[teamIdx];
        var names = new string[team.Count];
        for (int i = 0; i < team.Count; i++) names[i] = team[i].Name;
        return string.Join("/", names);
    }

}
