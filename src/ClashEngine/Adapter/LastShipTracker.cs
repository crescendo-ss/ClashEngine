using ClashEngine.Core.Identity;
using ClashEngine.Persistence;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Adapter;

/// <summary>
/// Zone-wide observer that keeps <see cref="PersistLastShipStore"/> current: whenever any player
/// drops from a ship to spectator mode -- or exits an arena / disconnects while still in a ship,
/// which fires no ship-&gt;spec change of its own -- the ship they were on is remembered as their
/// "last ship". <see cref="Orchestration.MatchOrchestrator"/> reads that memory at match
/// placement so players start each match in the ship they last flew instead of a fixed default.
/// </summary>
/// <remarks>
/// Deliberately not filtered to match participants: the whole point is that casual flying between
/// matches (pub arenas, warm-up, etc.) updates the preference too. Match-end force-specs funnel
/// through the same ship-&gt;spec transition, so finishing a match in a ship also remembers it.
/// </remarks>
public sealed class LastShipTracker
{
    private readonly IComponentBroker _broker;
    private readonly PersistLastShipStore _store;
    private readonly PlayerKeyResolver _resolver;
    private bool _registered;

    public LastShipTracker(IComponentBroker broker, PersistLastShipStore store, PlayerKeyResolver resolver)
    {
        _broker = broker;
        _store = store;
        _resolver = resolver;
    }

    public void Register()
    {
        if (_registered) return;
        PreShipFreqChangeCallback.Register(_broker, OnShipFreqChange);
        PlayerActionCallback.Register(_broker, OnPlayerAction);
        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        PreShipFreqChangeCallback.Unregister(_broker, OnShipFreqChange);
        PlayerActionCallback.Unregister(_broker, OnPlayerAction);
        _registered = false;
    }

    private void OnShipFreqChange(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
    {
        if (newShip != ShipType.Spec || oldShip == ShipType.Spec) return;
        if (_resolver.KeyOf(player) is not PlayerKey key) return;
        _store.Set(key, oldShip);
    }

    private void OnPlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        // An arena exit (or disconnect) while in a ship fires no ship->spec PreShipFreqChange
        // (Game's LeaveArena handler only clears spectator state), so capture here instead --
        // Player.Ship still holds the in-ship value until the next EnterArena resets it.
        if (action != PlayerAction.LeaveArena) return;
        if (player.Ship == ShipType.Spec) return;
        if (_resolver.KeyOf(player) is not PlayerKey key) return;
        _store.Set(key, player.Ship);
    }
}
