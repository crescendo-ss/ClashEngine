using System;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Events;

/// <summary>
/// Subscribes to SubspaceServer player-action / ship-freq callbacks and translates them into
/// pure-layer engine events. Kill events are routed separately via
/// <see cref="MatchKillRouter"/>. All callbacks fire on the mainloop thread.
/// </summary>
public sealed class PlayerStateObserver
{
    private const string LogCategory = nameof(PlayerStateObserver);

    private readonly IComponentBroker _broker;
    private readonly MatchmakingEngine _engine;
    private readonly PlayerKeyResolver _resolver;
    private readonly IClock _clock;
    private readonly ClashLog _log;

    /// <summary>
    /// Fires after the engine has been notified of a Connect, with the resolved
    /// <see cref="PlayerKey"/> and the connect timestamp. Subscribers run on the mainloop
    /// thread; long-running work (HTTP I/O for rating sync, for example) should be dispatched
    /// off-thread by the subscriber.
    /// </summary>
    public event Action<PlayerKey, DateTimeOffset>? PlayerConnected;

    public PlayerStateObserver(
        IComponentBroker broker,
        MatchmakingEngine engine,
        PlayerKeyResolver resolver,
        IClock clock,
        ClashLog log)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Register()
    {
        PlayerActionCallback.Register(_broker, OnPlayerAction);
        ShipFreqChangeCallback.Register(_broker, OnShipFreqChange);
    }

    public void Unregister()
    {
        PlayerActionCallback.Unregister(_broker, OnPlayerAction);
        ShipFreqChangeCallback.Unregister(_broker, OnShipFreqChange);
    }

    private void OnPlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        // Trace covers every wire event so we can replay the timing of an oddly-timed disconnect
        // or arena hop after the fact.
        if (_log.IsTrace)
            _log.Trace(LogCategory,
                $"PlayerAction({action}) {player.Name ?? "(no-name)"} arena={arena?.Name ?? "-"} status={player.Status}");

        switch (action)
        {
            case PlayerAction.Connect:
                _resolver.OnConnect(player);
                if (_resolver.KeyOf(player) is PlayerKey k1)
                {
                    var connectAt = _clock.UtcNow;
                    _engine.OnPlayerConnected(k1, connectAt);
                    if (_log.IsDebug)
                        _log.Debug(LogCategory, $"Connected: {k1.Name}");
                    // Fired AFTER engine state is updated so any subscriber that consults
                    // engine state (eligibility, queue membership, in-flight match) sees a
                    // consistent post-connect view. Exceptions are logged and swallowed --
                    // a misbehaving subscriber must not strand the connect callback.
                    Fire(PlayerConnected, k1, connectAt, "PlayerConnected");
                }
                break;

            case PlayerAction.Disconnect:
                if (_resolver.KeyOf(player) is PlayerKey k2)
                {
                    _engine.OnPlayerDisconnected(k2, _clock.UtcNow);
                    // A disconnect (or zone exit) is a hard departure from the party. Spec'ing
                    // is handled separately in OnShipFreqChange and deliberately does NOT drop.
                    _engine.LeaveGroup(k2, _clock.UtcNow);
                    // Drop pending invitations on either side so they don't outlive the session.
                    // Deliberately not done on LeaveArena -- arena hops are transient and the
                    // invitation should still be there when the player returns.
                    var pruned = _engine.Groups.RemoveInvitationsInvolving(k2);
                    if (_log.IsDebug)
                    {
                        var elig = _engine.CheckEligibility(k2).Status;
                        _log.Debug(LogCategory, $"Disconnected: {k2.Name} (eligibility now {elig}, pruned {pruned} invitation(s))");
                    }
                }
                _resolver.OnDisconnect(player);
                break;

            case PlayerAction.LeaveArena:
                if (_resolver.KeyOf(player) is PlayerKey k3)
                {
                    _engine.OnPlayerLeftArena(k3, _clock.UtcNow);
                    // Leaving the arena (e.g. ?go pub) drops the player from their party.
                    _engine.LeaveGroup(k3, _clock.UtcNow);
                    if (_log.IsDebug)
                        _log.Debug(LogCategory, $"LeaveArena: {k3.Name} arena={arena?.Name ?? "-"}");
                }
                break;
        }
    }

    private void OnShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
    {
        if (_resolver.KeyOf(player) is not PlayerKey key)
        {
            if (_log.IsTrace)
                _log.Trace(LogCategory,
                    $"ShipFreqChange (unresolved) {player.Name ?? "(no-name)"} {oldShip}->{newShip} freq {oldFreq}->{newFreq}");
            return;
        }

        bool wasSpec = oldShip == ShipType.Spec;
        bool isSpec = newShip == ShipType.Spec;
        var now = _clock.UtcNow;

        if (_log.IsTrace)
            _log.Trace(LogCategory,
                $"ShipFreqChange {key.Name} ship {oldShip}->{newShip} freq {oldFreq}->{newFreq} " +
                $"(spec-transition: {(wasSpec && !isSpec ? "->active" : !wasSpec && isSpec ? "->spec" : "none")})");

        if (wasSpec && !isSpec)
        {
            // Player joined a ship. Engine treats Pending->Active as first-join, InGrace->Active
            // as return; both calls are no-ops in unrelated states, so issue both.
            _engine.OnPlayerJoinedArena(key, now);
            _engine.OnPlayerReturned(key, now);
        }
        else if (!wasSpec && isSpec)
        {
            // Ship -> spec: the player is still in the arena, just on the spec freq, so route
            // to OnPlayerSpecced (preserves the in-match abandonment-grace bookkeeping but
            // doesn't drop queues). OnPlayerLeftArena is reserved for an actual arena exit
            // (the LeaveArena action above), where dropping queues is correct.
            _engine.OnPlayerSpecced(key, now);
        }
        // Ship-to-ship or freq-only changes are not currently meaningful to the engine.
    }

    // Kill events are routed by MatchKillRouter (which calls engine.OnKill on our behalf and
    // then fans out to readers in registration order). PlayerStateObserver no longer subscribes
    // to KillCallback directly.

    /// <summary>
    /// Invokes a (possibly null) lifecycle event with a single try/catch around the delegate
    /// invocation list. A throwing subscriber is logged at Error and the remaining subscribers
    /// still fire -- <c>Delegate.GetInvocationList</c> is iterated explicitly rather than
    /// relying on <c>?.Invoke</c> so one bad handler can't block the rest.
    /// </summary>
    private void Fire(Action<PlayerKey, DateTimeOffset>? evt, PlayerKey key, DateTimeOffset at, string label)
    {
        if (evt is null) return;
        foreach (var handler in evt.GetInvocationList())
        {
            try { ((Action<PlayerKey, DateTimeOffset>)handler)(key, at); }
            catch (Exception ex)
            {
                _log.Warn(LogCategory, $"{label} subscriber threw: {ex}");
            }
        }
    }
}
