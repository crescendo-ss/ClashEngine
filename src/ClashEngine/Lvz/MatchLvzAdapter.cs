using System;
using System.Collections.Generic;
using ClashEngine.Adapter;
using ClashEngine.Core;
using ClashEngine.Core.Adapter;
using ClashEngine.Core.Identity;
using ClashEngine.Core.Matches;
using ClashEngine.Core.Matching;
using ClashEngine.Core.Penalties;
using ClashEngine.Core.Queue;
using ClashEngine.Core.Stats;
using ClashEngine.Stats;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.TeamVersus;
using SS.Packets.Game;

namespace ClashEngine.Lvz;

/// <summary>
/// Bridges ClashEngine's match lifecycle into SubspaceServer's match-rendering pipeline. On a
/// match becoming Live this fires the <c>MatchStarting</c> / <c>MatchStarted</c> /
/// <c>TeamVersusMatchStarted</c> callbacks plus per-player <c>MatchAddPlaying</c> so the
/// <c>MatchLvz</c> module (and <c>MatchFocus</c>) take care of statbox / scoreboard rendering.
/// On match end the symmetric <c>TeamVersusMatchEnded</c> + <c>MatchRemovePlaying</c> +
/// <c>MatchEnded</c> sequence is fired.
/// </summary>
/// <remarks>
/// <para>This adapter does NOT register any direct LVZ work itself -- all the rendering lives
/// in SubspaceServer's <c>MatchLvz</c> module. We only feed it the <see cref="IMatchData"/>
/// view via the standard callbacks it already subscribes to.</para>
///
/// <para>One <see cref="ClashMatchData"/> is built per match at <see cref="OnMatchProposed"/>
/// (so it's available to <c>MatchStartingCallback</c> when MatchFocus needs it) and held until
/// <see cref="OnMatchEnded"/> tears it down.</para>
/// </remarks>
public sealed class MatchLvzAdapter : IMatchmakingTelemetry, IMatchFocusAdvisor
{
    private const string LogCategory = nameof(MatchLvzAdapter);

    private readonly IComponentBroker _broker;
    private readonly MatchmakingEngine _engine;
    private readonly MatchStatsRegistry _stats;
    private readonly PlayerKeyResolver _resolver;
    private readonly IArenaManager _arenaManager;
    private readonly ILogManager _log;

    private readonly Dictionary<Guid, QueueDefinition> _queueByMatch = new();
    private readonly Dictionary<Guid, ClashMatchData> _byMatch = new();
    private AdvisorRegistrationToken<IMatchFocusAdvisor>? _matchFocusAdvisorToken;
    private bool _registeredCallbacks;

    public MatchLvzAdapter(
        IComponentBroker broker,
        MatchmakingEngine engine,
        MatchStatsRegistry stats,
        PlayerKeyResolver resolver,
        IArenaManager arenaManager,
        ILogManager log)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Subscribes to the broker-scoped wire callbacks needed to drive MatchLvz refresh between
    /// match start and match end (spawns, item uses). Kill events are routed via
    /// <see cref="ClashEngine.Events.MatchKillRouter"/>, which guarantees the engine's
    /// <c>LivesRemaining</c> and the recorder's inventory are up-to-date before our handler runs.
    /// </summary>
    public void Register()
    {
        if (_registeredCallbacks) return;
        SpawnCallback.Register(_broker, OnSpawn);
        PlayerPositionPacketCallback.Register(_broker, OnPosition);
        // Register as IMatchFocusAdvisor so SS.Matchmaking.MatchFocus's SpectateChanged handler
        // can find ClashEngine matches (it walks broker advisors). MatchAddPlayingCallback fired
        // in OnMatchStarted already populates participant lists; this advisor only fills in the
        // SpectateChanged path where MatchFocus needs to map a target Player back to a match.
        _matchFocusAdvisorToken = _broker.RegisterAdvisor<IMatchFocusAdvisor>(this);
        _registeredCallbacks = true;
    }

    public void Unregister()
    {
        if (!_registeredCallbacks) return;
        SpawnCallback.Unregister(_broker, OnSpawn);
        PlayerPositionPacketCallback.Unregister(_broker, OnPosition);
        _broker.UnregisterAdvisor(ref _matchFocusAdvisorToken);
        _registeredCallbacks = false;
    }

    // --- IMatchFocusAdvisor ---

    bool IMatchFocusAdvisor.TryGetPlaying(IMatch match, HashSet<string> players)
    {
        if (match is not ClashMatchData cmd) return false;
        if (!_byMatch.TryGetValue(cmd.Match.MatchId, out var ours) || !ReferenceEquals(ours, cmd))
            return false;

        // "Playing" = currently rostered. Includes Active and InGrace; excludes Abandoned.
        // Lives-out players still appear here so MatchFocus retains them as participants and
        // the kill-filter / position-filter advisors keep working until match end.
        for (int t = 0; t < cmd.Match.Teams.Count; t++)
        {
            for (int j = 0; j < cmd.Match.Teams[t].Count; j++)
            {
                var key = cmd.Match.Teams[t][j];
                var status = cmd.Match.GetStatus(key);
                if (status == PlayerStatus.Abandoned) continue;
                players.Add(key.Name);
            }
        }
        return true;
    }

    IMatch? IMatchFocusAdvisor.GetMatch(Player player)
    {
        if (player is null) return null;
        if (_resolver.KeyOf(player) is not PlayerKey key) return null;
        var matchId = _stats.MatchIdOf(key);
        if (matchId is null) return null;
        return _byMatch.TryGetValue(matchId.Value, out var cmd) ? cmd : null;
    }

    public void OnMatchProposed(MatchProposal proposal)
    {
        // Cache the queue-by-matchId mapping; same trick ClashStatsTelemetry uses since the
        // engine doesn't carry queue refs on ActiveMatch. We materialize the IMatchData later
        // (in OnMatchStarted) once the match is actually Live -- MatchLvz only renders for
        // started matches and MatchFocus only adds tracking on MatchStarting fired here.
        if (!_engine.Queues.TryGet(proposal.QueueName, out var queueDef)) return;
        foreach (var (matchId, match) in _engine.ActiveMatches)
        {
            if (ReferenceEquals(match.Teams, proposal.Teams))
            {
                _queueByMatch[matchId] = queueDef;
                return;
            }
        }
    }

    public void OnMatchStarted(ActiveMatch match)
    {
        try
        {
            if (_byMatch.ContainsKey(match.MatchId)) return;
            if (!_queueByMatch.TryGetValue(match.MatchId, out var queue))
            {
                _log.LogM(LogLevel.Warn, LogCategory,
                    $"Match {match.MatchId:N} started but no queue captured; skipping LVZ adapter.");
                return;
            }
            // Skip silently if there's no MatchArena (no place to render).
            if (string.IsNullOrEmpty(queue.MatchArenaName)) return;

            // Pull the StatsRecorder so per-player Kills/Deaths surface in IMemberStats. The
            // stats registry creates the recorder on its own OnMatchStarted; ordering between
            // telemetries within CompositeTelemetry is the registration order, but we look up
            // by id rather than capturing so a later-firing stats register still works.
            var statsRecorder = _stats.ActiveRecorders.TryGetValue(match.MatchId, out var rec) ? rec : null;

            int arenaNumber = 0;
            if (_arenaManager.FindArena(queue.MatchArenaName) is { } arena)
                arenaNumber = arena.BaseName == arena.Name ? 0 : ParseArenaNumber(arena.Name, arena.BaseName);

            var matchData = new ClashMatchData(
                match, queue, statsRecorder, _resolver, _arenaManager, arenaNumber);
            _byMatch[match.MatchId] = matchData;

            // Order matters: MatchFocus subscribes to MatchStartingCallback to register the
            // match for tracking; subsequent MatchAddPlayingCallback calls then attach players
            // to it. MatchLvz then renders on TeamVersusMatchStartedCallback.
            MatchStartingCallback.Fire(_broker, matchData);
            for (int t = 0; t < match.Teams.Count; t++)
                for (int j = 0; j < match.Teams[t].Count; j++)
                {
                    var key = match.Teams[t][j];
                    var player = _resolver.Resolve(key);
                    MatchAddPlayingCallback.Fire(_broker, matchData, key.Name, player);
                }
            MatchStartedCallback.Fire(_broker, matchData);

            var ssArena = matchData.Arena;
            if (ssArena is not null)
                TeamVersusMatchStartedCallback.Fire(ssArena, matchData);
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"OnMatchStarted failed for match {match.MatchId:N}: {ex}");
        }
    }

    public void OnMatchEnded(MatchOutcome outcome)
    {
        // Always clean up the queue-by-match entry, symmetric with OnMatchProposed.
        _queueByMatch.Remove(outcome.MatchId);

        if (!_byMatch.Remove(outcome.MatchId, out var matchData)) return;

        try
        {
            var endReason = MapEndReason(outcome.FinalState);
            ITeam? winnerTeam = null;
            if (endReason == MatchEndReason.Decided && outcome.RankedTeams.Count > 0)
            {
                // The first ranked team is the winner; map back to the adapter ITeam by
                // matching on Players reference / first-player identity.
                var winners = outcome.RankedTeams[0].Players;
                for (int t = 0; t < matchData.Teams.Count; t++)
                {
                    if (winners.Count > 0 && matchData.Match.Teams[t].Count > 0
                        && matchData.Match.Teams[t][0].Equals(winners[0]))
                    {
                        winnerTeam = matchData.Teams[t];
                        break;
                    }
                }
            }

            var ssArena = matchData.Arena;
            if (ssArena is not null)
                TeamVersusMatchEndedCallback.Fire(ssArena, matchData, endReason, winnerTeam);

            for (int t = 0; t < matchData.Match.Teams.Count; t++)
                for (int j = 0; j < matchData.Match.Teams[t].Count; j++)
                {
                    var key = matchData.Match.Teams[t][j];
                    var player = _resolver.Resolve(key);
                    MatchRemovePlayingCallback.Fire(_broker, matchData, key.Name, player);
                }

            MatchEndedCallback.Fire(_broker, matchData);
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"OnMatchEnded failed for match {outcome.MatchId:N}: {ex}");
        }
    }

    /// <summary>Best-effort ArenaNumber extraction from the arena name. SubspaceServer arenas
    /// are conventionally named "<c>{base}{number}</c>" for public sub-arenas, or just
    /// "<c>{base}</c>" with no suffix. MatchLvz uses ArenaNumber only for MatchIdentifier
    /// uniqueness across boxes -- 0 is a sensible default.</summary>
    private static int ParseArenaNumber(string fullName, string baseName)
    {
        if (fullName.Length <= baseName.Length) return 0;
        var suffix = fullName.AsSpan(baseName.Length);
        return int.TryParse(suffix, out var n) ? n : 0;
    }

    private static MatchEndReason MapEndReason(MatchState state) => state switch
    {
        MatchState.Completed => MatchEndReason.Decided,
        MatchState.Cancelled => MatchEndReason.Cancelled,
        MatchState.Abandoned => MatchEndReason.Cancelled,
        _ => MatchEndReason.Cancelled,
    };

    /// <summary>
    /// Broker-scoped kill handler. For every kill where both players belong to one of our
    /// managed matches, fans the event into MatchLvz via the two arena-scoped callbacks it
    /// subscribes to (<c>TeamVersusMatchPlayerKilled</c> for slot/lives state,
    /// <c>TeamVersusStatsPlayerKilled</c> for the simple-mode K/D refresh).
    /// </summary>
    /// <remarks>
    /// Engine state -- particularly <c>LivesRemaining[killed]</c> -- already reflects the kill
    /// by the time the router calls us; we read live state here without an ordering hazard.
    /// </remarks>
    public void OnKill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
    {
        try
        {
            if (_resolver.KeyOf(killer) is not PlayerKey killerKey) return;
            if (_resolver.KeyOf(killed) is not PlayerKey killedKey) return;
            if (!TryFindSlots(killedKey, killerKey, out var matchData, out var killedSlot, out var killerSlot))
                return;

            // matchData.Arena is the configured match arena; use it rather than the kill's
            // arena so the LVZ callback fires on the right scope even if a stray cross-arena
            // kill ever surfaces (it shouldn't, but it's free defense).
            var ssArena = matchData.Arena;
            if (ssArena is null) return;

            bool isKnockout = matchData.Match.LivesPerPlayer.HasValue
                && matchData.Match.LivesRemaining.TryGetValue(killedKey, out var lives)
                && lives == 0;

            TeamVersusMatchPlayerKilledCallback.Fire(ssArena, killedSlot, killerSlot, isKnockout);
            // Stats are read live off the slots themselves (ClashPlayerSlot implements
            // IMemberStats), so the same instance can serve both the slot and stats arguments.
            // Cast through object since IPlayerSlot doesn't extend IMemberStats; the slot is
            // always a ClashPlayerSlot which implements both.
            var killedStats = (IMemberStats)killedSlot;
            var killerStats = (IMemberStats)killerSlot;
            TeamVersusStatsPlayerKilledCallback.Fire(
                ssArena, killedSlot, killedStats, killerSlot, killerStats, isKnockout);
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory, $"OnKill fan-out failed: {ex}");
        }
    }

    /// <summary>
    /// Spawn handler. The recorder resets a player's inventory to the ship's initial loadout
    /// on every spawn, so we tell MatchLvz to refresh every item column for that slot.
    /// </summary>
    private void OnSpawn(Player player, SpawnCallback.SpawnReason reason)
    {
        try
        {
            if (_resolver.KeyOf(player) is not PlayerKey key) return;
            if (!TryFindSlot(key, out var matchData, out var slot)) return;
            var ssArena = matchData.Arena;
            if (ssArena is null) return;

            const ItemChanges allItems =
                ItemChanges.Bursts | ItemChanges.Repels | ItemChanges.Thors |
                ItemChanges.Bricks | ItemChanges.Decoys | ItemChanges.Rockets | ItemChanges.Portals;
            TeamVersusMatchPlayerItemsChangedCallback.Fire(ssArena, slot, allItems);
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory, $"OnSpawn fan-out failed: {ex}");
        }
    }

    /// <summary>
    /// Position-packet handler. When a packet carries an item-use weapon (Repel/Burst/Decoy),
    /// fires <c>TeamVersusMatchPlayerItemsChangedCallback</c> for the matching column so MatchLvz
    /// refreshes the statbox right after the recorder decrements inventory. StatsListener is
    /// registered first and updates the recorder synchronously on the same callback, so reading
    /// <c>slot.Bursts</c>/etc. here yields the post-decrement count.
    /// </summary>
    private void OnPosition(Player player, ref readonly C2S_PositionPacket packet, ref readonly ExtraPositionData extra, bool hasExtra)
    {
        try
        {
            if (packet.Weapon.Type == WeaponCodes.Null) return;
            var ev = WeaponMapping.FromPositionPacket(packet.Weapon);
            if (!ev.IsItemUse) return;

            if (_resolver.KeyOf(player) is not PlayerKey key) return;
            if (!TryFindSlot(key, out var matchData, out var slot)) return;
            var ssArena = matchData.Arena;
            if (ssArena is null) return;

            var changes = ItemKindToFlag(ev.Item!.Value);
            if (changes == ItemChanges.None) return;
            TeamVersusMatchPlayerItemsChangedCallback.Fire(ssArena, slot, changes);
        }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory, $"OnPosition fan-out failed: {ex}");
        }
    }

    private static ItemChanges ItemKindToFlag(ItemKind item) => item switch
    {
        ItemKind.Burst => ItemChanges.Bursts,
        ItemKind.Repel => ItemChanges.Repels,
        ItemKind.Decoy => ItemChanges.Decoys,
        ItemKind.Thor => ItemChanges.Thors,
        ItemKind.Brick => ItemChanges.Bricks,
        ItemKind.Rocket => ItemChanges.Rockets,
        ItemKind.Portal => ItemChanges.Portals,
        _ => ItemChanges.None,
    };

    private bool TryFindSlot(PlayerKey key, out ClashMatchData matchData, out IPlayerSlot slot)
    {
        foreach (var (_, md) in _byMatch)
        {
            for (int t = 0; t < md.Match.Teams.Count; t++)
            {
                var team = md.Match.Teams[t];
                for (int j = 0; j < team.Count; j++)
                {
                    if (team[j] == key)
                    {
                        matchData = md;
                        slot = md.Teams[t].Slots[j];
                        return true;
                    }
                }
            }
        }
        matchData = null!;
        slot = null!;
        return false;
    }

    private bool TryFindSlots(
        PlayerKey killedKey,
        PlayerKey killerKey,
        out ClashMatchData matchData,
        out IPlayerSlot killedSlot,
        out IPlayerSlot killerSlot)
    {
        if (!TryFindSlot(killedKey, out matchData, out killedSlot))
        {
            killerSlot = null!;
            return false;
        }
        // Both players must be in the same match for the kill to count toward MatchLvz refresh.
        for (int t = 0; t < matchData.Match.Teams.Count; t++)
        {
            var team = matchData.Match.Teams[t];
            for (int j = 0; j < team.Count; j++)
            {
                if (team[j] == killerKey)
                {
                    killerSlot = matchData.Teams[t].Slots[j];
                    return true;
                }
            }
        }
        killerSlot = null!;
        return false;
    }

    // Other IMatchmakingTelemetry events default to no-op via the interface; LVZ refresh on
    // kill / spawn happens via the broker callbacks above instead.
}
