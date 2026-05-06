using System;
using System.Collections.Generic;
using System.Threading;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
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

    /// <summary>
    /// Grace window between the kill packet and the deferred attribution + chat broadcast.
    /// Mirrors the 200 ms wait in <c>SS.Matchmaking.TeamVersusStats.PlayerKilledAsync</c> --
    /// Continuum batches outbound damage so the killing-blow C2S Damage packet can land just
    /// after the C2S Die packet. Reading the recovery state synchronously inside the kill
    /// callback would miss that late damage and yield killer-damage of 0 / low values.
    /// </summary>
    public const int LateDamageGraceMs = 200;

    private readonly IComponentBroker _broker;
    private readonly MatchmakingEngine _engine;
    private readonly MatchStatsRegistry _registry;
    private readonly IPlayerData _playerData;
    private readonly PlayerKeyResolver _resolver;
    private readonly EmpShutdownLookup _empLookup;
    private readonly KillFeedReporter? _killFeedReporter;
    private readonly IMainloopTimer _timer;
    private readonly ILogManager _log;

    /// <summary>
    /// Pending kill finalizations keyed by match. Drained by <see cref="DrainPendingForMatch"/>
    /// at match end so the upload sees the late-damage attribution. The value list grows when
    /// kills come in faster than 200 ms apart on the same match (typical in 4v4).
    /// </summary>
    private readonly Dictionary<Guid, List<DeferredKill>> _pendingKills = new();
    private TimerDelegate<DeferredKill>? _onDeferredKillFinalize;

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
        IMainloopTimer timer,
        ILogManager log)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _empLookup = empLookup ?? throw new ArgumentNullException(nameof(empLookup));
        _killFeedReporter = killFeedReporter;
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Register()
    {
        if (_registered) return;
        _onDamage = OnPlayerDamage;
        _onPosition = OnPlayerPositionPacket;
        _onSpawn = OnSpawn;
        _onGreen = OnGreen;
        _onDeferredKillFinalize = OnDeferredKillFinalize;

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

        // Flush any pending deferred attributions synchronously so we don't leak unfinalized
        // kills on shutdown (would leave PlayerStats.KillDamage/Assist short of the final kill
        // and the recovery state populated with no consumer).
        if (_onDeferredKillFinalize is not null)
        {
            foreach (var matchId in new List<Guid>(_pendingKills.Keys))
                DrainPendingForMatch(matchId);
        }

        _onDamage = null;
        _onPosition = null;
        _onSpawn = null;
        _onGreen = null;
        _onDeferredKillFinalize = null;
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

        // Wire-authoritative inventory: when Continuum included ExtraPositionData (gated by the
        // server-side AddExtraPositionDataWatch installed by MatchLvzAdapter), mirror the
        // reported item counts straight into the recorder. Replaces the reconstructive decrement
        // model that was off-by-one on repels and never fired for rockets / bricks / portals.
        if (hasExtra)
        {
            recorder.OnExtraPositionData(
                pkey,
                bursts: extra.Bursts,
                repels: extra.Repels,
                thors: extra.Thors,
                bricks: extra.Bricks,
                decoys: extra.Decoys,
                rockets: extra.Rockets,
                portals: extra.Portals);
        }

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

    /// <summary>Called by <see cref="ClashEngine.Events.MatchKillRouter"/> as a pre-engine
    /// reader -- runs <em>before</em> <c>engine.OnKill</c>. This ordering is required because
    /// <c>engine.OnKill</c> can synchronously finalize the match (firing <c>OnMatchEnded</c>,
    /// which tears down the recorder); recording the kill afterward would silently drop the
    /// match-ending death. As a consequence, <c>LivesRemaining</c>/<c>ExitedAt</c> still hold
    /// the pre-kill state when we read them, so knockout detection has to predict from
    /// remaining lives.
    ///
    /// <para>The kill is split across two phases. The lifecycle work (death/knockout/kill
    /// counters, life closure) runs synchronously here so it lands before <c>engine.OnKill</c>
    /// processes the match-ending case. The damage-attribution work (KillDamage / KillCredit /
    /// Assist tallies + the chat broadcast) is deferred by <see cref="LateDamageGraceMs"/> ms
    /// to give Continuum time to deliver the killing-blow C2S Damage packet, which is often
    /// batched a few ticks behind the C2S Die packet -- mirrors the 200 ms wait in
    /// SS.Matchmaking.TeamVersusStats.PlayerKilledAsync. Match-end drains any still-pending
    /// finalizations synchronously so the upload sees the late-damage stats.</para></summary>
    public void OnKill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
    {
        if (_resolver.KeyOf(killer) is not PlayerKey kkey) return;
        if (_resolver.KeyOf(killed) is not PlayerKey vkey) return;
        var recorder = _registry.RecorderFor(vkey);
        if (recorder is null) return;

        uint tick = (uint)ServerTick.Now;
        bool isKnockout = IsKnockout(vkey);

        // Phase 1: lifecycle (synchronous). Must happen before engine.OnKill so kill counters
        // and life closures are recorded before a match-ending kill triggers FinalizeMatch.
        if (!recorder.BeginKill(vkey, kkey, tick, isKnockout)) return;

        // Capture the dynamic match values that engine.OnKill will mutate, so the deferred
        // broadcast still displays the pre-engine values (lives pre-decrement, score pre-add).
        var matchIdOpt = _registry.MatchIdOf(vkey);
        if (matchIdOpt is not Guid matchId) return;
        if (!_engine.ActiveMatches.TryGetValue(matchId, out var match)) return;

        var pre = CapturePreState(match, vkey);
        var context = new KillFeedMatchContext(match.Teams, match.LivesPerPlayer, match.StartedAt);

        // Phase 2: schedule the damage-attribution + chat broadcast for after the late-damage
        // grace window. Pending entry held under matchId so DrainPendingForMatch can flush it
        // synchronously at match end (recorder is alive, but the upload payload is built right
        // after EndMatch, so the late attribution must land before that).
        var pending = new DeferredKill(matchId, recorder, arena, vkey, kkey, tick, pre, context);
        if (!_pendingKills.TryGetValue(matchId, out var list))
        {
            list = new List<DeferredKill>();
            _pendingKills[matchId] = list;
        }
        list.Add(pending);

        if (_onDeferredKillFinalize is not null)
            _timer.SetTimer(_onDeferredKillFinalize, LateDamageGraceMs, Timeout.Infinite, pending, this);
    }

    /// <summary>
    /// Drain any deferred kill finalizations queued for <paramref name="matchId"/>, running
    /// them synchronously and cancelling their pending timers. Intended to be called by the
    /// match-end path before the recorder is removed and its payload is uploaded -- otherwise
    /// the timer would fire after upload and the late-damage stats would never reach storage.
    /// </summary>
    public void DrainPendingForMatch(Guid matchId)
    {
        if (!_pendingKills.TryGetValue(matchId, out var list)) return;
        // Snapshot to a local list because each Finalize call may otherwise be re-entered
        // through the timer callback (defensive -- the mainloop is single-threaded today).
        var pending = list.ToArray();
        _pendingKills.Remove(matchId);

        if (_onDeferredKillFinalize is not null)
            _timer.ClearTimer(_onDeferredKillFinalize, this);

        foreach (var dk in pending)
        {
            try { FinalizeKill(dk); }
            catch (Exception ex)
            {
                _log.LogM(LogLevel.Error, LogCategory,
                    $"Drain finalize for match {matchId:N} failed ({dk.Victim.Name} kb {dk.Killer.Name}): {ex}");
            }
        }

        // ClearTimer above cancelled timers for ALL matches. Re-arm anything that was for a
        // different match so we don't lose those pending finalizations.
        if (_pendingKills.Count > 0 && _onDeferredKillFinalize is not null)
        {
            foreach (var (_, otherList) in _pendingKills)
                foreach (var dk in otherList)
                    _timer.SetTimer(_onDeferredKillFinalize, LateDamageGraceMs, Timeout.Infinite, dk, this);
        }
    }

    private bool OnDeferredKillFinalize(DeferredKill dk)
    {
        if (_pendingKills.TryGetValue(dk.MatchId, out var list))
        {
            list.Remove(dk);
            if (list.Count == 0) _pendingKills.Remove(dk.MatchId);
        }
        try { FinalizeKill(dk); }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"Deferred finalize for match {dk.MatchId:N} failed ({dk.Victim.Name} kb {dk.Killer.Name}): {ex}");
        }
        return false;  // one-shot
    }

    private void FinalizeKill(DeferredKill dk)
    {
        var snapshot = dk.Recorder.FinalizeKillAttribution(dk.Victim, dk.Killer, dk.AtTick);
        if (snapshot is null) return;
        if (_killFeedReporter is null) return;
        _killFeedReporter.Report(dk.Arena, dk.MatchId, dk.Victim, dk.Killer, snapshot, dk.PreState, dk.MatchContext);
    }

    private static KillFeedPreState CapturePreState(ActiveMatch match, PlayerKey victim)
    {
        int livesPre = -1;
        if (match.LivesPerPlayer.HasValue && match.LivesRemaining.TryGetValue(victim, out var lr))
            livesPre = lr;
        bool exitedPre = match.ExitedAt.ContainsKey(victim);

        // Defensive copy of the per-team kill counts -- ActiveMatch.KillsByTeam is the live
        // collection that engine.OnKill mutates; we need the pre-kill snapshot for the score
        // line.
        var scores = new int[match.KillsByTeam.Count];
        for (int i = 0; i < scores.Length; i++) scores[i] = match.KillsByTeam[i];
        return new KillFeedPreState(livesPre, exitedPre, scores);
    }

    /// <summary>Closure passed through <see cref="IMainloopTimer"/> to the deferred finalize
    /// callback. Captures the recorder reference directly so the kill is finalized even if the
    /// match has been removed from <see cref="MatchStatsRegistry"/> by the time the timer
    /// fires (e.g. when the match-ending kill drained late, between EndMatch and timer-fire).</summary>
    private sealed record DeferredKill(
        Guid MatchId,
        StatsRecorder Recorder,
        Arena Arena,
        PlayerKey Victim,
        PlayerKey Killer,
        uint AtTick,
        KillFeedPreState PreState,
        KillFeedMatchContext MatchContext);

    /// <summary>
    /// Predicts whether the in-flight kill will be the eliminating one. Runs pre-engine, so
    /// <c>LivesRemaining</c> still holds the count of respawns the player had <em>before</em>
    /// this death -- 0 means there's no respawn left to consume and the engine will set
    /// <c>ExitedAt</c>. Returns <c>false</c> for unlimited-lives matches.
    /// </summary>
    private bool IsKnockout(PlayerKey victim)
    {
        var matchId = _registry.MatchIdOf(victim);
        if (matchId is null) return false;
        if (!_engine.ActiveMatches.TryGetValue(matchId.Value, out var match)) return false;
        if (match.LivesPerPlayer is null) return false;
        if (match.ExitedAt.ContainsKey(victim)) return true;
        if (!match.LivesRemaining.TryGetValue(victim, out var lives)) return false;
        return lives == 0;
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
