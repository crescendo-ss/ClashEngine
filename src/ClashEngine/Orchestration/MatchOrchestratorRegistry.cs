using System;
using System.Collections.Generic;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace ClashEngine.Orchestration;

/// <summary>
/// Telemetry listener that owns one <see cref="MatchOrchestrator"/> per active match. Also owns
/// the <see cref="PlayerPositionPacketCallback"/> subscription used by orchestrators in their
/// staging phase to detect AFK players.
/// </summary>
public sealed class MatchOrchestratorRegistry : IMatchmakingTelemetry
{
    private readonly Dictionary<Guid, MatchOrchestrator> _orchestrators = new();
    private readonly Dictionary<PlayerKey, MatchOrchestrator> _stagingPlayers = new();
    private readonly IComponentBroker _broker;
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
    private readonly ClashEngine.Adapter.SpawnSettingsApplier? _spawnApplier;
    private readonly ClashEngine.Adapter.NoItemsSettingsApplier? _noItemsApplier;
    private bool _registeredCallback;

    public MatchOrchestratorRegistry(
        IComponentBroker broker,
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
        MatchStatsRegistry? matchStats = null,
        ClashEngine.Adapter.SpawnSettingsApplier? spawnApplier = null,
        ClashEngine.Adapter.NoItemsSettingsApplier? noItemsApplier = null)
    {
        _broker = broker;
        _engine = engine;
        _game = game;
        _chat = chat;
        _timer = timer;
        _arenaManager = arenaManager;
        _clock = clock;
        _log = log;
        _resolver = resolver;
        _verbose = verbose ?? throw new ArgumentNullException(nameof(verbose));
        _audience = audience;
        _freqAllocator = freqAllocator;
        _matchStats = matchStats;
        _spawnApplier = spawnApplier;
        _noItemsApplier = noItemsApplier;
    }

    public void Register()
    {
        if (_registeredCallback) return;
        PlayerPositionPacketCallback.Register(_broker, OnPositionPacket);
        PlayerActionCallback.Register(_broker, OnPlayerAction);
        // PreShipFreqChange (not ShipFreqChange) so the orchestrator's leave-snapshot capture is
        // synchronous with the ship change. The async ShipFreqChange runs on a later mainloop
        // iteration -- if a player presses Esc-S and immediately types ?return, the chat command
        // would otherwise be processed before the snapshot was taken, and the returner would get
        // the slot's default ship instead of whichever ship they were last on.
        PreShipFreqChangeCallback.Register(_broker, OnShipFreqChange);
        _registeredCallback = true;
    }

    public void Unregister()
    {
        if (!_registeredCallback) return;
        PlayerPositionPacketCallback.Unregister(_broker, OnPositionPacket);
        PlayerActionCallback.Unregister(_broker, OnPlayerAction);
        PreShipFreqChangeCallback.Unregister(_broker, OnShipFreqChange);
        _registeredCallback = false;
    }

    public void OnMatchProposed(MatchProposal proposal)
    {
        if (!_engine.Queues.TryGet(proposal.QueueName, out var queueDef)) return;

        Guid matchId = FindMatchId(proposal);
        if (matchId == Guid.Empty) return;

        var orchestrator = new MatchOrchestrator(
            matchId, queueDef, proposal, _engine, _game, _chat, _timer, _arenaManager, _clock, _log,
            _resolver, _verbose, _audience, _freqAllocator, matchStats: _matchStats, broker: _broker,
            spawnApplier: _spawnApplier, noItemsApplier: _noItemsApplier);
        _orchestrators[matchId] = orchestrator;

        // Track players for position-packet routing during staging.
        for (int t = 0; t < proposal.Teams.Count; t++)
            for (int j = 0; j < proposal.Teams[t].Count; j++)
                _stagingPlayers[proposal.Teams[t][j]] = orchestrator;

        orchestrator.BeginSetup();
    }

    public void OnMatchEnded(MatchOutcome outcome)
    {
        if (!_orchestrators.Remove(outcome.MatchId, out var orchestrator)) return;

        // Clear staging-tracking entries for this match.
        var toRemove = new List<PlayerKey>();
        foreach (var kvp in _stagingPlayers)
            if (ReferenceEquals(kvp.Value, orchestrator)) toRemove.Add(kvp.Key);
        foreach (var k in toRemove) _stagingPlayers.Remove(k);

        // Cancelled state currently only comes from the staging no-show path, which has already
        // broadcast a tailored "Match cancelled. {names} left / did not ready..." line in
        // OnStagingEnd. Pass null here so Cleanup doesn't follow it with a redundant generic
        // "Match cancelled."
        var summary = outcome.FinalState switch
        {
            MatchState.Completed when outcome.RankedTeams.Count > 0 =>
                $"Match over! Team {string.Join("/", outcome.RankedTeams[0].Players)} wins.",
            MatchState.Cancelled => null,
            MatchState.Abandoned => "Match abandoned.",
            _ => "Match ended.",
        };
        orchestrator.Cleanup(summary);
    }

    private void OnPositionPacket(Player player, ref readonly C2S_PositionPacket packet,
        ref readonly ExtraPositionData extra, bool hasExtra)
    {
        if (_resolver.KeyOf(player) is not PlayerKey key) return;
        if (!_stagingPlayers.TryGetValue(key, out var orchestrator)) return;
        orchestrator.OnPositionPacket(key, packet.Rotation, packet.X, packet.Y, packet.Weapon.Type);
    }

    /// <summary>
    /// Routes ship-to-spec transitions for in-match participants to the owning orchestrator so it
    /// can freeze a return-snapshot (last live ship + items at the moment of leave). Other
    /// transitions (spec->ship, ship->ship) are not interesting to the orchestrator -- spec->ship
    /// is handled either by <see cref="MatchOrchestrator.OnPlayerEnteredArena"/> on initial setup
    /// or by <see cref="MatchOrchestrator.TryReturn"/> on <c>?return</c>.
    /// </summary>
    private void OnShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
    {
        if (newShip != ShipType.Spec || oldShip == ShipType.Spec) return;
        if (_resolver.KeyOf(player) is not PlayerKey key) return;
        var orchestrator = OrchestratorFor(key);
        if (orchestrator is null) return;
        orchestrator.OnPlayerSpecced(key, oldShip);
    }

    /// <summary>
    /// Routes a kill to the orchestrator owning the victim's match so it can apply per-queue
    /// knockout-spec timing. Called by <see cref="ClashEngine.Events.MatchKillRouter"/> after
    /// the engine has decremented <c>LivesRemaining</c> -- the router guarantees that ordering.
    /// </summary>
    public void OnKill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
    {
        if (_resolver.KeyOf(killed) is not PlayerKey victim) return;
        foreach (var orchestrator in _orchestrators.Values)
        {
            if (orchestrator.OwnsPlayer(victim))
            {
                orchestrator.OnKill(victim);
                return;
            }
        }
    }

    /// <summary>
    /// Dispatches EnterArena events to the right orchestrator so it can finish ship/freq setup
    /// for any of its players who were SendToArena'd at match-setup time. Other PlayerAction
    /// values are ignored here; PlayerStateObserver handles the engine-side events.
    /// </summary>
    private void OnPlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        if (action != PlayerAction.EnterArena || arena is null) return;
        if (_resolver.KeyOf(player) is not PlayerKey key) return;
        if (!_stagingPlayers.TryGetValue(key, out var orchestrator)) return;
        orchestrator.OnPlayerEnteredArena(key, arena);
    }

    /// <summary>
    /// Returns the orchestrator that owns <paramref name="key"/>, or <see langword="null"/> if no
    /// active orchestrator has them on a roster. Used by the <c>?return</c> command to dispatch
    /// the re-placement call.
    /// </summary>
    public MatchOrchestrator? OrchestratorFor(PlayerKey key)
    {
        foreach (var orchestrator in _orchestrators.Values)
            if (orchestrator.OwnsPlayer(key)) return orchestrator;
        return null;
    }

    public void OnTeamCollapsing(ActiveMatch m, int teamIdx, DateTimeOffset since, DateTimeOffset forfeitAt)
    {
        if (_orchestrators.TryGetValue(m.MatchId, out var orchestrator))
            orchestrator.OnTeamCollapsing(teamIdx, forfeitAt - since);
    }

    public void OnTeamRecovered(ActiveMatch m, int teamIdx)
    {
        if (_orchestrators.TryGetValue(m.MatchId, out var orchestrator))
            orchestrator.OnTeamRecovered(teamIdx);
    }

    // Other IMatchmakingTelemetry events default to no-op via the interface.

    private Guid FindMatchId(MatchProposal proposal)
    {
        foreach (var kvp in _engine.ActiveMatches)
        {
            if (ReferenceEquals(kvp.Value.Teams, proposal.Teams)) return kvp.Key;
        }
        foreach (var kvp in _engine.ActiveMatches)
        {
            if (_orchestrators.ContainsKey(kvp.Key)) continue;
            if (kvp.Value.Teams.Count != proposal.Teams.Count) continue;
            return kvp.Key;
        }
        return Guid.Empty;
    }
}
