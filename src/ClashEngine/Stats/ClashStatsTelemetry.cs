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
using ClashEngine.Core.Ratings;
using ClashEngine.Core.Stats;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Utilities;

namespace ClashEngine.Stats;

/// <summary>
/// <see cref="IMatchmakingTelemetry"/> consumer that drives <see cref="MatchStatsRegistry"/>
/// lifecycle from engine match events. On <see cref="OnMatchStarted"/> it opens an accounting
/// bracket for the match and registers each participant with their ship-derived parameters
/// (max energy, recharge rate, weapon energy config). On <see cref="OnMatchEnded"/> it closes
/// the bracket so any open lives are sealed with <see cref="LifeEndReason.MatchEnded"/>.
/// </summary>
public sealed class ClashStatsTelemetry : IMatchmakingTelemetry
{
    private const string LogCategory = nameof(ClashStatsTelemetry);

    private readonly MatchmakingEngine _engine;
    private readonly MatchStatsRegistry _registry;
    private readonly IConfigManager _config;
    private readonly IArenaManager _arenaManager;
    private readonly IClock _clock;
    private readonly IMatchUploader _uploader;
    private readonly ILogManager _log;
    private readonly IChat _chat;
    private readonly PlayerKeyResolver _resolver;
    private readonly MatchDamageWatcher _damageWatcher;
    private readonly Func<Guid, string?>? _recordingPathLookup;

    // Captured at OnMatchProposed by walking ActiveMatches for the proposal's Teams reference.
    // Read at OnMatchStarted to look up ship config. Same pattern MatchOrchestratorRegistry uses.
    private readonly Dictionary<Guid, QueueDefinition> _queueByMatch = new();

    // Captured at OnMatchStarted: the engine clears ActiveMatches before firing OnMatchEnded,
    // so we stash everything the upload payload needs (teams + start tick + game type).
    private readonly Dictionary<Guid, MatchInfo> _matchInfoById = new();

    public ClashStatsTelemetry(
        MatchmakingEngine engine,
        MatchStatsRegistry registry,
        IConfigManager config,
        IArenaManager arenaManager,
        IClock clock,
        IMatchUploader uploader,
        ILogManager log,
        IChat chat,
        PlayerKeyResolver resolver,
        IWatchDamage? watchDamage = null,
        Func<Guid, string?>? recordingPathLookup = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _damageWatcher = new MatchDamageWatcher(watchDamage, _resolver);
        _recordingPathLookup = recordingPathLookup;
    }

    public void OnMatchProposed(MatchProposal proposal)
    {
        if (!_engine.Queues.TryGet(proposal.QueueName, out var queueDef)) return;
        // Match is created before this fires -- find its id by Teams reference equality.
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
        if (!_queueByMatch.TryGetValue(match.MatchId, out var queue))
        {
            _log.LogM(LogLevel.Warn, LogCategory,
                $"Match {match.MatchId:N} started but no queue definition captured; stats not initialized.");
            return;
        }

        ConfigHandle? ch = ResolveArenaConfig(queue.MatchArenaName);
        uint atTick = (uint)ServerTick.Now;

        _registry.BeginMatch(match.MatchId);

        for (int t = 0; t < match.Teams.Count; t++)
        {
            for (int j = 0; j < match.Teams[t].Count; j++)
            {
                var player = match.Teams[t][j];
                ShipType ship = ResolveShip(queue, t, j);

                int maxEnergy = ch is null ? 1000 : ShipEnergyConfigBuilder.GetMaxEnergy(_config, ch, ship);
                double recharge = ch is null ? 0.0 : ShipEnergyConfigBuilder.GetRechargeRatePerTick(_config, ch, ship);
                var energyConfig = ch is null
                    ? new WeaponEnergyConfig(new Dictionary<WeaponKind, (int, int)>(), 0)
                    : ShipEnergyConfigBuilder.Build(_config, ch, ship);
                var initialInventory = ch is null
                    ? null
                    : ShipInventoryConfigBuilder.BuildInitial(_config, ch, ship);
                var maxInventory = ch is null
                    ? null
                    : ShipInventoryConfigBuilder.BuildMax(_config, ch, ship);

                _registry.AddPlayer(
                    match.MatchId, player, t, maxEnergy, recharge, energyConfig, atTick,
                    initialInventory, maxInventory);
            }
        }

        // Gate on PlayerDamageCallback: it only fires for players with at least one outstanding
        // damage-callback watch, so we have to add one per participant or DamageDealt /
        // DamageTaken / HitCount stay at zero. Released symmetrically in OnMatchEnded.
        _damageWatcher.WatchAll(match.Teams);

        // Snapshot per-player ratings at match start so the upload reflects pre-match skill
        // (the engine applies outcome to the store after the match -- by upload time the live
        // store has already moved on). Players with no prior rated match get Rating.Default
        // (GamesPlayed=0 flags them as new for downstream consumers) so the scoreboard's Δ
        // column has a pre value for every row instead of showing "—" only for new players.
        var ratingsAtStart = new Dictionary<PlayerKey, RatingPayload>();
        for (int t = 0; t < match.Teams.Count; t++)
        {
            for (int j = 0; j < match.Teams[t].Count; j++)
            {
                var key = match.Teams[t][j];
                var r = _engine.Ratings.TryGet(key, match.GameType, out var existing)
                    ? existing
                    : Rating.Default;
                ratingsAtStart[key] = new RatingPayload(r.Mu, r.Sigma, r.GamesPlayed);
            }
        }

        _matchInfoById[match.MatchId] = new MatchInfo(
            QueueName: queue.Name,
            GameType: match.GameType.Value,
            Arena: queue.MatchArenaName,
            Teams: match.Teams,
            StartedAt: match.StartedAt,
            RatingsAtStart: ratingsAtStart);
    }

    public void OnMatchEnded(MatchOutcome outcome)
    {
        uint atTick = (uint)ServerTick.Now;
        var recorder = _registry.EndMatch(outcome.MatchId, atTick);

        if (_matchInfoById.TryGetValue(outcome.MatchId, out var endingInfo))
            _damageWatcher.UnwatchAll(endingInfo.Teams);

        if (recorder is not null && _matchInfoById.TryGetValue(outcome.MatchId, out var info))
        {
            // Snapshot post-match ordinals BEFORE rating snapshots are needed by the formatter.
            // The engine has already applied the rating update by the time OnMatchEnded fires
            // (FinalizeMatch -> ApplyOutcome -> telemetry.OnMatchEnded), so reading the live
            // store here gives the new rating; pre-match is in info.RatingsAtStart.
            var postOrdinalByName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var postOrdinalByKey = new Dictionary<PlayerKey, double>();
            foreach (var team in info.Teams)
            {
                for (int j = 0; j < team.Count; j++)
                {
                    var key = team[j];
                    if (_engine.Ratings.TryGet(key, Core.Identity.GameTypeId.From(info.GameType), out var r))
                    {
                        postOrdinalByName[key.Name] = r.Ordinal;
                        postOrdinalByKey[key] = r.Ordinal;
                    }
                }
            }

            MatchPayload? payload = null;
            try
            {
                string? recordingPath = _recordingPathLookup?.Invoke(outcome.MatchId);
                payload = MatchExporter.Build(
                    matchId: outcome.MatchId,
                    queueName: info.QueueName,
                    gameType: info.GameType,
                    arena: info.Arena,
                    teams: info.Teams,
                    startedAt: info.StartedAt,
                    recorder: recorder,
                    outcome: outcome,
                    ratingsAtStart: info.RatingsAtStart,
                    postOrdinalByPlayer: postOrdinalByKey,
                    recordingPath: recordingPath);
                _uploader.Upload(payload);
            }
            catch (Exception ex)
            {
                _log.LogM(LogLevel.Error, LogCategory,
                    $"Failed to build/upload match payload for {outcome.MatchId:N}: {ex}");
            }

            if (payload is not null)
            {
                BroadcastScoreboard(payload, postOrdinalByName, info.Teams);
                BroadcastStatsUrl(payload.MatchId, info.Arena);
            }
        }

        _queueByMatch.Remove(outcome.MatchId);
        _matchInfoById.Remove(outcome.MatchId);
    }

    /// <summary>
    /// If <c>[ClashEngine] StatsViewUrl</c> is configured, format it with the match id and
    /// broadcast the resulting line as an arena message after the scoreboard. The template may
    /// contain a <c>{matchId}</c> placeholder (replaced with the dashed GUID); absent that, the
    /// id is appended.
    /// </summary>
    private void BroadcastStatsUrl(Guid matchId, string? arenaName)
    {
        var template = _config.GetStr(_config.Global, "ClashEngine", "StatsViewUrl");
        if (string.IsNullOrWhiteSpace(template)) return;

        var arena = string.IsNullOrEmpty(arenaName) ? null : _arenaManager.FindArena(arenaName);
        if (arena is null) return;

        string formattedId = matchId.ToString("D");
        string url = template.Contains("{matchId}", StringComparison.Ordinal)
            ? template.Replace("{matchId}", formattedId)
            : template + formattedId;
        _chat.SendArenaMessage(arena, $"Match stats: {url}");
    }

    /// <summary>
    /// Sends the formatted scoreboard to every resolvable participant. Failures are logged but
    /// do not propagate -- a chat hiccup must not break match teardown.
    /// </summary>
    private void BroadcastScoreboard(
        MatchPayload payload,
        IReadOnlyDictionary<string, double> postOrdinalByName,
        IReadOnlyList<IReadOnlyList<Core.Identity.PlayerKey>> teams)
    {
        IReadOnlyList<string> lines;
        try { lines = ScoreboardFormatter.Format(payload, postOrdinalByName); }
        catch (Exception ex)
        {
            _log.LogM(LogLevel.Error, LogCategory,
                $"Failed to format scoreboard for {payload.MatchId:N}: {ex}");
            return;
        }

        for (int t = 0; t < teams.Count; t++)
        {
            for (int j = 0; j < teams[t].Count; j++)
            {
                var p = _resolver.Resolve(teams[t][j]);
                if (p is null) continue;
                for (int i = 0; i < lines.Count; i++) _chat.SendMessage(p, lines[i]);
            }
        }
    }

    private ConfigHandle? ResolveArenaConfig(string? arenaName)
    {
        if (string.IsNullOrEmpty(arenaName)) return null;
        var arena = _arenaManager.FindArena(arenaName);
        return arena?.Cfg;
    }

    private static ShipType ResolveShip(QueueDefinition queue, int teamIdx, int slotIdx)
    {
        if (queue.ShipBySlot is null) return ShipType.Warbird;
        if (teamIdx >= queue.ShipBySlot.Count) return ShipType.Warbird;
        var team = queue.ShipBySlot[teamIdx];
        if (slotIdx >= team.Count) return ShipType.Warbird;
        int raw = team[slotIdx];
        return raw is >= 0 and <= 7 ? (ShipType)raw : ShipType.Warbird;
    }

    private readonly record struct MatchInfo(
        string QueueName,
        int GameType,
        string? Arena,
        IReadOnlyList<IReadOnlyList<PlayerKey>> Teams,
        DateTimeOffset? StartedAt,
        IReadOnlyDictionary<PlayerKey, RatingPayload> RatingsAtStart);
}
