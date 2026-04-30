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
using SS.Core;
using SS.Core.ComponentInterfaces;
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
    private const int LockTimeoutSeconds = 30;

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
        IRandomSource? rng = null)
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
        _rng = rng ?? DefaultRandomSource.Instance;
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

        for (int t = 0; t < _proposal.Teams.Count; t++)
        {
            short freq = QueueDefinition.FreqOf(t);
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

        BroadcastToAll(
            $"Match found! Move or fire within {(int)_queue.StagingDuration.TotalSeconds} seconds to confirm. Spec to abandon.");
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
        _game.Lock(player, notify: false, spec: false, timeout: LockTimeoutSeconds);

        if (_verbose.IsDebug)
            _verbose.Debug(LogCategory,
                $"Match {_matchId:N}: placed {key.Name} on {info.Ship} freq {info.Freq} at ({info.SpawnX},{info.SpawnY}).");
    }

    private static bool IsInArena(Player player, string arenaName) =>
        player.Arena is { } a && string.Equals(a.Name, arenaName, StringComparison.OrdinalIgnoreCase);

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
                string cancelMessage = $"Match cancelled. {afkNames} did not ready.";
                if (_audience is not null)
                    _audience.Broadcast(_matchId, _queue.MatchArenaName, notAfkParticipants, cancelMessage);
                else
                    foreach (var p in notAfkParticipants) _chat.SendMessage(p, cancelMessage);

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
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"Match {_matchId:N}: OnStagingEnd failed: {ex}");
        }
        return false;
    }

    /// <summary>Match has ended -- unlock and return players to spec.</summary>
    public void Cleanup(string summary)
    {
        SetPhase(MatchPhase.Cleanup);
        _timer.ClearTimer(OnStagingEnd, this);
        _timer.ClearTimer(OnCountdownTick, this);
        // Cancel any in-flight knockout-spec deferrals so we don't race with the immediate
        // match-end spec below (TState-typed and untyped variants are tracked separately).
        _timer.ClearTimer<PlayerKey>(OnDeferredKnockoutSpec, this);
        _pendingKnockoutSpec.Clear();

        // Broadcast the summary to everyone (participants + focused spectators) before
        // returning the participants to spec so watchers learn the outcome too.
        BroadcastToAll(summary);

        for (int t = 0; t < _proposal.Teams.Count; t++)
        {
            for (int j = 0; j < _proposal.Teams[t].Count; j++)
            {
                if (_resolver.Resolve(_proposal.Teams[t][j]) is { } p)
                {
                    _game.Unlock(p, notify: false);
                    _game.SetShip(p, ShipType.Spec);
                }
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
            // Final re-warp to the chosen spawn at GO. This (a) snaps any drift-clamped player
            // back to the exact spawn coord, and (b) ensures the whole team starts the match
            // co-located even if the drift check missed a sub-threshold wander.
            for (int t = 0; t < _proposal.Teams.Count; t++)
            {
                var spawn = _drift.ChosenSpawn(t);
                if (spawn.X == 0 && spawn.Y == 0) continue;   // no spawn configured
                for (int j = 0; j < _proposal.Teams[t].Count; j++)
                {
                    if (_resolver.Resolve(_proposal.Teams[t][j]) is { } p)
                        _game.WarpTo(p, spawn.X, spawn.Y);
                }
            }
            for (int t = 0; t < _proposal.Teams.Count; t++)
            {
                for (int j = 0; j < _proposal.Teams[t].Count; j++)
                {
                    if (_resolver.Resolve(_proposal.Teams[t][j]) is { } p)
                        _game.Unlock(p, notify: false);
                }
            }
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
