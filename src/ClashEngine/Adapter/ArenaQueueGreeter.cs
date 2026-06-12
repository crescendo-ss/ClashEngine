using ClashEngine.Commands;
using ClashEngine.Core;
using ClashEngine.Core.Eligibility;
using ClashEngine.Core.Identity;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace ClashEngine.Adapter;

/// <summary>
/// Greets every player entering an arena with the naked-<c>?queue</c> listing (the aligned
/// label / id / shape / waiting table), so the arena's matchmaking surface is discoverable
/// without knowing the command. Hooks <see cref="PlayerAction.EnterGame"/> -- not EnterArena --
/// so the lines land after the client has the map and is actually watching chat, instead of
/// racing the arena transfer. Arenas that own no queues greet with nothing, and players who are
/// in an active match are skipped (a participant warped into the match arena doesn't need the
/// queue table mid-staging, and a <c>?return</c>er doesn't need it mid-recovery).
/// </summary>
public sealed class ArenaQueueGreeter
{
    private readonly IComponentBroker _broker;
    private readonly MatchmakingEngine _engine;
    private readonly MatchmakingCommands _commands;
    private readonly PlayerKeyResolver _resolver;
    private bool _registered;

    public ArenaQueueGreeter(
        IComponentBroker broker,
        MatchmakingEngine engine,
        MatchmakingCommands commands,
        PlayerKeyResolver resolver)
    {
        _broker = broker;
        _engine = engine;
        _commands = commands;
        _resolver = resolver;
    }

    public void Register()
    {
        if (_registered) return;
        PlayerActionCallback.Register(_broker, OnPlayerAction);
        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        PlayerActionCallback.Unregister(_broker, OnPlayerAction);
        _registered = false;
    }

    private void OnPlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        if (action != PlayerAction.EnterGame || arena is null) return;
        if (_resolver.KeyOf(player) is not PlayerKey key) return;
        if (_engine.CheckEligibility(key).Status == EligibilityStatus.InMatch) return;
        _commands.ListAllQueues(player, arena, silentWhenEmpty: true);
    }
}
