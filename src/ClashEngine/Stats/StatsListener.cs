using System;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;

namespace ClashEngine.Stats;

/// <summary>
/// Subscribes to SubspaceServer wire events at the broker scope and translates them into
/// <see cref="StatsRecorder"/> calls via the per-match registry. One instance per module.
/// </summary>
/// <remarks>
/// <para>The <see cref="MatchStatsRegistry"/> handles dispatch -- events for players not in any
/// active match return <c>null</c> from <c>RecorderFor</c> and are silently dropped.</para>
/// <para><b>Knockout detection.</b> Read after <see cref="MatchmakingEngine"/> has processed
/// the kill (which decrements lives on <see cref="ActiveMatch"/>); a victim with 0 lives
/// remaining is a knockout. This requires <see cref="PlayerStateObserver"/> to have registered
/// before us so its <c>KillCallback</c> handler runs first.</para>
/// </remarks>
public sealed class StatsListener
{
    private const string LogCategory = nameof(StatsListener);

    private readonly IComponentBroker _broker;
    private readonly MatchmakingEngine _engine;
    private readonly MatchStatsRegistry _registry;
    private readonly IPlayerData _playerData;
    private readonly PlayerKeyResolver _resolver;
    private readonly EmpShutdownLookup _empLookup;
    private readonly KillFeedReporter? _killFeedReporter;
    private readonly ILogManager _log;

    private PlayerDamageCallback.PlayerDamageDelegate? _onDamage;
    private PlayerPositionPacketCallback.PlayerPositionPacketDelegate? _onPosition;
    private SpawnCallback.SpawnDelegate? _onSpawn;
    private GreenCallback.GreenDelegate? _onGreen;
    private bool _registered;

    public StatsListener(
        IComponentBroker broker,
        MatchmakingEngine engine,
        MatchStatsRegistry registry,
        IPlayerData playerData,
        PlayerKeyResolver resolver,
        EmpShutdownLookup empLookup,
        KillFeedReporter? killFeedReporter,
        ILogManager log)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _empLookup = empLookup ?? throw new ArgumentNullException(nameof(empLookup));
        _killFeedReporter = killFeedReporter;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Register()
    {
        if (_registered) return;
        _onDamage = OnPlayerDamage;
        _onPosition = OnPlayerPositionPacket;
        _onSpawn = OnSpawn;
        _onGreen = OnGreen;

        PlayerDamageCallback.Register(_broker, _onDamage);
        PlayerPositionPacketCallback.Register(_broker, _onPosition);
        SpawnCallback.Register(_broker, _onSpawn);
        GreenCallback.Register(_broker, _onGreen);
        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        if (_onDamage is not null) PlayerDamageCallback.Unregister(_broker, _onDamage);
        if (_onPosition is not null) PlayerPositionPacketCallback.Unregister(_broker, _onPosition);
        if (_onSpawn is not null) SpawnCallback.Unregister(_broker, _onSpawn);
        if (_onGreen is not null) GreenCallback.Unregister(_broker, _onGreen);

        _onDamage = null;
        _onPosition = null;
        _onSpawn = null;
        _onGreen = null;
        _registered = false;
    }

    private void OnPlayerDamage(Player victim, ServerTick timestamp, ReadOnlySpan<DamageData> damages)
    {
        if (_resolver.KeyOf(victim) is not PlayerKey vkey) return;
        var recorder = _registry.RecorderFor(vkey);
        if (recorder is null) return;

        uint tick = (uint)timestamp;
        for (int i = 0; i < damages.Length; i++)
        {
            ref readonly var d = ref damages[i];
            if (d.Damage <= 0) continue;

            var weaponKind = WeaponMapping.FromDamage(d.WeaponData);
            if (weaponKind is null) continue;

            var attacker = _playerData.PidToPlayer(d.AttackerPlayerId);
            if (attacker is null) continue;
            if (_resolver.KeyOf(attacker) is not PlayerKey akey) continue;

            uint emp = _empLookup.For(attacker.Arena, attacker.Ship, d.WeaponData.Type);
            recorder.OnDamage(vkey, akey, d.Damage, weaponKind.Value, empDurationTicks: emp, atTick: tick);
        }
    }

    private void OnPlayerPositionPacket(
        Player player,
        ref readonly C2S_PositionPacket packet,
        ref readonly ExtraPositionData extra,
        bool hasExtra)
    {
        if (_resolver.KeyOf(player) is not PlayerKey pkey) return;
        var recorder = _registry.RecorderFor(pkey);
        if (recorder is null) return;

        // Drive the active-tick accountant from every packet, even ones with no weapon event.
        uint tick = (uint)packet.Time;
        recorder.OnPositionPacket(pkey, tick, packet.Energy);

        if (packet.Weapon.Type == WeaponCodes.Null) return;

        var ev = WeaponMapping.FromPositionPacket(packet.Weapon);
        if (ev.IsNone) return;

        if (ev.IsWeaponFire)
        {
            recorder.OnWeaponFired(pkey, ev.Weapon!.Value, packet.Weapon.Level + 1, ev.Multifire, tick);
        }
        else if (ev.IsItemUse)
        {
            recorder.OnItemUsed(pkey, ev.Item!.Value, tick);
        }
    }

    private void OnSpawn(Player player, SpawnCallback.SpawnReason reason)
    {
        if (_resolver.KeyOf(player) is not PlayerKey pkey) return;
        var recorder = _registry.RecorderFor(pkey);
        if (recorder is null) return;
        recorder.OnSpawn(pkey, (uint)ServerTick.Now);
    }

    /// <summary>Called by <see cref="ClashEngine.Events.MatchKillRouter"/> after the engine has
    /// updated <c>LivesRemaining</c>; we can read knockout state directly off the match.</summary>
    public void OnKill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
    {
        if (_resolver.KeyOf(killer) is not PlayerKey kkey) return;
        if (_resolver.KeyOf(killed) is not PlayerKey vkey) return;
        var recorder = _registry.RecorderFor(vkey);
        if (recorder is null) return;

        uint tick = (uint)ServerTick.Now;
        // Snapshot attribution BEFORE recorder.OnKill so the recovery state still has the
        // unrecovered damage entries (OnKill clears them at the end).
        var feed = _killFeedReporter is not null
            ? recorder.BuildKillFeed(vkey, kkey, tick)
            : null;

        bool isKnockout = IsKnockout(vkey);
        recorder.OnKill(vkey, kkey, tick, isKnockout);

        if (feed is not null && _killFeedReporter is not null)
        {
            var matchId = _registry.MatchIdOf(vkey);
            if (matchId is Guid mid)
                _killFeedReporter.Report(arena, mid, vkey, kkey, feed);
        }
    }

    private bool IsKnockout(PlayerKey victim)
    {
        var matchId = _registry.MatchIdOf(victim);
        if (matchId is null) return false;
        if (!_engine.ActiveMatches.TryGetValue(matchId.Value, out var match)) return false;
        if (match.LivesPerPlayer is null) return false;
        return match.ExitedAt.ContainsKey(victim);
    }

    /// <summary>
    /// Player picked up a green. Maps the prize to one of the stockpilable
    /// <see cref="ItemKind"/>s and increments the recorder's inventory; non-stockpilable
    /// prizes (Recharge, Energy, Shield, etc.) are silently ignored. Multiprize is expanded
    /// to one increment of every stockpilable kind, matching Continuum's behavior.
    /// </summary>
    private void OnGreen(Player player, int x, int y, Prize prize)
    {
        if (_resolver.KeyOf(player) is not PlayerKey pkey) return;
        var recorder = _registry.RecorderFor(pkey);
        if (recorder is null) return;

        if (prize == Prize.Multiprize)
        {
            recorder.OnPrizePickup(pkey, ItemKind.Burst);
            recorder.OnPrizePickup(pkey, ItemKind.Repel);
            recorder.OnPrizePickup(pkey, ItemKind.Decoy);
            recorder.OnPrizePickup(pkey, ItemKind.Thor);
            recorder.OnPrizePickup(pkey, ItemKind.Brick);
            recorder.OnPrizePickup(pkey, ItemKind.Rocket);
            recorder.OnPrizePickup(pkey, ItemKind.Portal);
            return;
        }

        var item = MapPrizeToItem(prize);
        if (item is null) return;
        recorder.OnPrizePickup(pkey, item.Value);
    }

    private static ItemKind? MapPrizeToItem(Prize prize) => prize switch
    {
        Prize.Burst => ItemKind.Burst,
        Prize.Repel => ItemKind.Repel,
        Prize.Decoy => ItemKind.Decoy,
        Prize.Thor => ItemKind.Thor,
        Prize.Brick => ItemKind.Brick,
        Prize.Rocket => ItemKind.Rocket,
        Prize.Portal => ItemKind.Portal,
        _ => null,
    };
}
