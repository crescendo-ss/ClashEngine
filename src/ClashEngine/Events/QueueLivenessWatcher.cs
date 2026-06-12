using System;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Queue;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;

namespace ClashEngine.Events;

/// <summary>
/// Auto-refreshes queued players' AFK dwell clocks from in-game activity, so a present player
/// never needs to re-issue <c>?play</c> to dodge the AFK cull. Activity = a deliberate input:
/// ship rotation <em>change</em>, any weapon fire, or any chat message. The decision logic
/// (rotation anchor, forward cooldown, queued-membership) lives in the unit-tested
/// <see cref="QueueActivityTracker"/>; this class is the SS shim around it.
/// </summary>
/// <remarks>
/// <para>
/// Membership comes from the telemetry composite: <see cref="OnQueueAdded"/> plus the two
/// post-match re-enqueue events that fire in its place (<c>OnWinnerPromoted</c>,
/// <c>OnAutoQueued</c>) cover every add path, and <see cref="OnQueueRemoved"/> every remove
/// path, so the per-position-packet cost for a non-queued player is one dictionary miss. Spectators never reach
/// <see cref="PlayerPositionPacketCallback"/> (the SS Game module only fires it for ship
/// players), so a queued spectator proves liveness via chat or <c>?play</c> only.
/// </para>
/// <para>All callbacks fire on the mainloop thread, same as every other engine entry point.</para>
/// </remarks>
public sealed class QueueLivenessWatcher : IMatchmakingTelemetry
{
    private const string LogCategory = nameof(QueueLivenessWatcher);

    private readonly IComponentBroker _broker;
    private readonly MatchmakingEngine _engine;
    private readonly PlayerKeyResolver _resolver;
    private readonly IClock _clock;
    private readonly ClashLog _log;
    private readonly QueueActivityTracker _tracker = new();
    private bool _registered;

    public QueueLivenessWatcher(
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
        if (_registered) return;
        PlayerPositionPacketCallback.Register(_broker, OnPositionPacket);
        ChatMessageCallback.Register(_broker, OnChatMessage);
        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        PlayerPositionPacketCallback.Unregister(_broker, OnPositionPacket);
        ChatMessageCallback.Unregister(_broker, OnChatMessage);
        _registered = false;
    }

    // ---- queue membership (driven by the engine through the telemetry composite)

    void IMatchmakingTelemetry.OnQueueAdded(PlayerKey player, string queueName, DateTimeOffset at, PlayerKey? initiator)
        => _tracker.OnQueueAdded(player, queueName);

    // Post-match re-enqueues (KOTH winner promotion, ?auto) fire these in place of OnQueueAdded,
    // so they're membership-adds here too -- otherwise a re-queued player's in-game activity
    // never refreshes their dwell clock and they get AFK-warned/culled while visibly present.
    void IMatchmakingTelemetry.OnWinnerPromoted(PlayerKey player, string queueName, DateTimeOffset at,
        int defensesUsed, int maxDefenses, bool sentToBack)
        => _tracker.OnQueueAdded(player, queueName);

    void IMatchmakingTelemetry.OnAutoQueued(PlayerKey player, string queueName, DateTimeOffset at)
        => _tracker.OnQueueAdded(player, queueName);

    void IMatchmakingTelemetry.OnQueueRemoved(PlayerKey player, string queueName, DateTimeOffset at, QueueRemovalReason reason)
        => _tracker.OnQueueRemoved(player, queueName);

    // ---- activity signals

    private void OnPositionPacket(Player player, ref readonly C2S_PositionPacket packet,
        ref readonly ExtraPositionData extra, bool hasExtra)
    {
        if (_resolver.KeyOf(player) is not PlayerKey key) return;
        bool weaponFired = packet.Weapon.Type != WeaponCodes.Null;
        if (_tracker.OnPositionSignal(key, packet.Rotation, weaponFired, _clock.UtcNow))
            Forward(key);
    }

    private void OnChatMessage(Arena? arena, Player? player, ChatMessageType type, ChatSound sound,
        Player? toPlayer, short freq, ReadOnlySpan<char> message)
    {
        // Null player = server/arena-originated message, not a liveness signal. Commands
        // (?play etc.) never reach this callback; ?play has its own refresh path.
        if (player is null) return;
        if (_resolver.KeyOf(player) is not PlayerKey key) return;
        if (_tracker.OnDeliberateSignal(key, _clock.UtcNow))
            Forward(key);
    }

    private void Forward(PlayerKey key)
    {
        bool refreshed = _engine.OnPlayerActivity(key, _clock.UtcNow);
        // Naturally rate-limited: the tracker forwards at most once per cooldown per player.
        if (refreshed && _log.IsDebug)
            _log.Debug(LogCategory, $"QueueActivityRefresh: {key.Name}");
    }
}
